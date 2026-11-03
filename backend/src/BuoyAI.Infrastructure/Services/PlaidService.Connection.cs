using Going.Plaid;
using Going.Plaid.Entity;
using Going.Plaid.Institutions;
using Going.Plaid.Item;
using Going.Plaid.Link;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs.Plaid;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Partial class containing connection management methods:
/// - Link token creation and public token exchange
/// - Connection/disconnection/reconnection
/// - Update mode (re-authentication)
/// - Connection status management
/// </summary>
public partial class PlaidService
{
  /// <summary>
  /// Plaid Link country codes from Plaid:CountryCodes config (default US-only).
  /// Canada launch (#941) is a config change — ["US","CA"] — not a deploy: US
  /// Plaid accounts have US+CA production access by default, and sandbox exposes
  /// all countries (test CA with institution ins_43). Unknown values are skipped
  /// loudly rather than failing link-token creation.
  /// </summary>
  private CountryCode[] ConfiguredCountryCodes()
  {
    var configured = _configuration.GetSection("Plaid:CountryCodes").Get<string[]>();
    if (configured == null || configured.Length == 0)
    {
      return new[] { CountryCode.Us };
    }

    var codes = new List<CountryCode>();
    foreach (var raw in configured)
    {
      if (Enum.TryParse<CountryCode>(raw, ignoreCase: true, out var code))
      {
        codes.Add(code);
      }
      else
      {
        _logger.LogWarning("Ignoring unknown Plaid country code in config: {Code}", raw);
      }
    }
    return codes.Count > 0 ? codes.ToArray() : new[] { CountryCode.Us };
  }

  /// <summary>
  /// OAuth banks (Chase, Bank of America, Wells Fargo, Capital One...) send the user to the
  /// bank's own login and need a way back into the app afterwards: the app's package name on
  /// Android, a registered redirect_uri (a Universal Link) on iOS. Desktop web needs nothing.
  /// Applied to fresh connects AND update mode: Plaid's update-mode guide asks for the same
  /// redirect config for OAuth re-auth, and update tokens used to carry none, so reconnecting
  /// Chase on Android could not return to the app.
  /// </summary>
  internal static void ApplyPlatformOAuth(
    LinkTokenCreateRequest linkTokenRequest, string? platform, IConfiguration configuration)
  {
    if (string.Equals(platform, "android", StringComparison.OrdinalIgnoreCase))
    {
      var androidPackageName = configuration["Plaid:AndroidPackageName"];
      if (!string.IsNullOrWhiteSpace(androidPackageName))
      {
        linkTokenRequest.AndroidPackageName = androidPackageName;
      }
    }
    else if (string.Equals(platform, "ios", StringComparison.OrdinalIgnoreCase))
    {
      // https://buoy-ai.com/app/plaid-oauth in production (on the Plaid dashboard's Allowed
      // redirect URIs, and in the AASA file). If Plaid ever rejects it, the link token is
      // retried without it (CreateLinkTokenWithRedirectFallbackAsync) rather than failing.
      var iosRedirectUri = configuration["Plaid:IosRedirectUri"];
      if (!string.IsNullOrWhiteSpace(iosRedirectUri))
      {
        linkTokenRequest.RedirectUri = iosRedirectUri;
      }
    }
  }

  /// <summary>
  /// link/token/create, retried once without redirect_uri when Plaid rejects a request that
  /// carried one. The redirect is only needed for OAuth banks (Chase and the like) on iOS; if
  /// it is ever missing from the dashboard's Allowed redirect URIs (or mistyped), Plaid refuses
  /// the whole token, which would take down every iOS bank connect, not just OAuth ones. The
  /// failure is logged and sent to Sentry so the misconfiguration is visible.
  /// </summary>
  private async Task<LinkTokenCreateResponse> CreateLinkTokenWithRedirectFallbackAsync(
    LinkTokenCreateRequest linkTokenRequest, int userId, string? platform)
  {
    var response = await _plaidClient.LinkTokenCreateAsync(linkTokenRequest);
    if ((response.Error is not null || string.IsNullOrEmpty(response.LinkToken))
        && !string.IsNullOrEmpty(linkTokenRequest.RedirectUri))
    {
      _logger.LogError(
        "Plaid rejected link/token/create with redirect_uri {RedirectUri} for user {UserId} (platform {Platform}): {ErrorCode} {ErrorMessage} (request_id: {RequestId}). Retrying without it; OAuth banks will not work until the redirect is fixed.",
        linkTokenRequest.RedirectUri, userId, platform,
        response.Error?.ErrorCode, response.Error?.ErrorMessage, response.RequestId);
      SentrySdk.CaptureMessage(
        $"Plaid redirect_uri rejected ({response.Error?.ErrorCode}); retried without it (platform {platform}, request_id {response.RequestId})",
        SentryLevel.Error);
      linkTokenRequest.RedirectUri = null;
      response = await _plaidClient.LinkTokenCreateAsync(linkTokenRequest);
    }
    return response;
  }

  public async Task<CreateLinkTokenResponse> CreateLinkTokenAsync(CreateLinkTokenRequest request)
  {
    _metrics.RecordPlaidLinkStarted();
    try
    {
      var user = await _userRepository.GetByIdAsync(request.UserId);
      if (user == null)
        throw new ArgumentException("User not found");

      var linkTokenRequest = new LinkTokenCreateRequest
      {
        ClientName = _clientName,
        Language = Language.English,
        CountryCodes = ConfiguredCountryCodes(),
        User = new LinkTokenCreateRequestUser
        {
          ClientUserId = user.Id.ToString()
        },
        Products = new[] { Products.Transactions },
        Webhook = _configuration["Plaid:WebhookUrl"]
      };

      ApplyPlatformOAuth(linkTokenRequest, request.Platform, _configuration);

      var response = await CreateLinkTokenWithRedirectFallbackAsync(linkTokenRequest, request.UserId, request.Platform);

      // Going.Plaid does NOT throw on Plaid API errors — it returns a response whose
      // Error is set and whose LinkToken is null. Silently passing that through gave
      // clients `200 {"linkToken": null}`, which surfaced as an unexplainable
      // "Failed to open Plaid Link" (2026-07-18: android_package_name wasn't registered
      // in the Plaid dashboard and NOTHING was logged). Fail loudly instead.
      if (response.Error is not null || string.IsNullOrEmpty(response.LinkToken))
      {
        // request_id is what Plaid support + the Activity log key on — always include it.
        _logger.LogError(
          "Plaid link/token/create failed for user {UserId} (platform {Platform}): {ErrorType}/{ErrorCode} — {ErrorMessage} (request_id: {RequestId})",
          request.UserId, request.Platform,
          response.Error?.ErrorType, response.Error?.ErrorCode, response.Error?.ErrorMessage,
          response.RequestId);
        // The controller converts the throw below into a 4xx (handled), so without an
        // explicit capture this class of outage never reaches Sentry — exactly how the
        // unregistered-android-package failure stayed invisible for two weeks.
        SentrySdk.CaptureMessage(
          $"Plaid link/token/create failed: {response.Error?.ErrorCode} (platform {request.Platform}, request_id {response.RequestId})",
          SentryLevel.Error);
        throw new InvalidOperationException(
          $"Plaid link token creation failed: {response.Error?.ErrorCode.ToString() ?? "empty token"} — {response.Error?.ErrorMessage ?? "no error detail"}");
      }

      return new CreateLinkTokenResponse
      {
        LinkToken = response.LinkToken
      };
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error creating Plaid link token for user {UserId}", request.UserId);
      throw;
    }
  }

  public async Task<ExchangePublicTokenResponse> ExchangePublicTokenAsync(ExchangePublicTokenRequest request)
  {
    try
    {
      var exchangeRequest = new ItemPublicTokenExchangeRequest
      {
        PublicToken = request.PublicToken
      };

      var response = await _plaidClient.ItemPublicTokenExchangeAsync(exchangeRequest);

      // Get user to check for family membership
      var user = await _userRepository.GetByIdAsync(request.UserId);

      // Store the connection with FamilyId if user is in a family
      var connection = new PlaidConnection
      {
        UserId = request.UserId,
        FamilyId = user?.FamilyId,
        Provider = BankProvider.Plaid,
        AccessToken = _encryptionService.Encrypt(response.AccessToken),
        ItemId = response.ItemId,
        IsActive = true
      };

      // Get institution info
      try
      {
        var itemRequest = new ItemGetRequest { AccessToken = response.AccessToken };
        var itemResponse = await _plaidClient.ItemGetAsync(itemRequest);
        
        if (string.IsNullOrEmpty(itemResponse.Item.InstitutionId))
        {
          _logger.LogWarning("InstitutionId is null or empty for item {ItemId}", response.ItemId);
        }
        else
        {
          var institutionRequest = new InstitutionsGetByIdRequest
          {
            InstitutionId = itemResponse.Item.InstitutionId,
            CountryCodes = ConfiguredCountryCodes()
          };
          var institutionResponse = await _plaidClient.InstitutionsGetByIdAsync(institutionRequest);

          connection.InstitutionId = itemResponse.Item.InstitutionId;
          connection.InstitutionName = institutionResponse.Institution.Name;

          // Check for duplicate institution for this user
          var existingConnections = await _plaidConnectionRepository.GetByUserIdAsync(request.UserId);
          var duplicateConnection = existingConnections.FirstOrDefault(c => 
            c.IsActive && 
            c.InstitutionId == itemResponse.Item.InstitutionId);

          if (duplicateConnection != null)
          {
            // The duplicate Item ALREADY exists at Plaid (created by the exchange above).
            // Its access token is never persisted, so this is the only moment it can ever
            // be removed — skipping this leaves an orphaned Item billing forever
            // (Plaid duplicate-items guidance + $0.30/Item/month).
            try
            {
              await _plaidClient.ItemRemoveAsync(new ItemRemoveRequest
              {
                AccessToken = response.AccessToken
              });
              _logger.LogInformation(
                "Removed duplicate Plaid item {ItemId} for user {UserId} (institution {InstitutionId} already connected)",
                response.ItemId, request.UserId, itemResponse.Item.InstitutionId);
            }
            catch (Exception rmEx)
            {
              _logger.LogWarning(rmEx,
                "Failed to remove duplicate Plaid item {ItemId} for user {UserId} — orphaned item may keep billing",
                response.ItemId, request.UserId);
            }

            throw new InvalidOperationException($"You already have an active connection to {institutionResponse.Institution.Name}. Please use the sync/refresh button to update your existing connection, or disconnect the existing connection first if you want to re-link your account.");
          }
        }
      }
      catch (InvalidOperationException)
      {
        throw;
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Could not get institution info for item {ItemId}", response.ItemId);
      }

      await _plaidConnectionRepository.CreateAsync(connection);
      _metrics.RecordPlaidLinkCompleted();

      // Sync accounts immediately
      await SyncAccountsAsync(request.UserId, response.AccessToken);

      // The plaintext access token is deliberately NOT surfaced on the response — it is a
      // server-only secret (already stored encrypted above). Callers that need it re-decrypt
      // from the persisted connection.
      return new ExchangePublicTokenResponse
      {
        ItemId = response.ItemId
      };
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error exchanging public token for user {UserId}", request.UserId);
      _metrics.RecordPlaidLinkFailed(ex.GetType().Name);
      throw;
    }
  }

  public async Task<ExchangePublicTokenResponse> ExchangePublicTokenWithQuickSyncAsync(ExchangePublicTokenRequest request)
  {
    try
    {
      // First do the standard token exchange
      var response = await ExchangePublicTokenAsync(request);
      
      // Get the connection that was just created
      var connection = await _plaidConnectionRepository.GetByItemIdAsync(response.ItemId);
      if (connection == null)
      {
        throw new InvalidOperationException($"Connection not found after creation for item {response.ItemId}");
      }

      // Re-decrypt the access token from the persisted connection rather than reading it off
      // the response DTO (which no longer carries the plaintext secret).
      var accessToken = _encryptionService.Decrypt(connection.AccessToken);

      // Import last 30 days immediately (quick sync)
      var endDate = DateTime.UtcNow;
      var startDate = endDate.AddDays(-30);

      try
      {
        await ImportHistoricalDataAsync(accessToken, startDate, endDate);
        
        connection.QuickSyncCompletedAt = DateTime.UtcNow;
        connection.ImportStatus = ImportStatus.Pending;
        connection.ImportMessage = "Quick sync completed. Full historical import will continue in background.";
      }
      catch (Exception quickSyncEx)
      {
        _logger.LogWarning(quickSyncEx, "Quick sync failed for connection {ConnectionId}, will retry in background", connection.Id);
        connection.QuickSyncCompletedAt = null;
        connection.ImportStatus = ImportStatus.Pending;
        connection.ImportMessage = "Initial transaction sync pending. Transactions will be imported in background.";
      }
      
      await _plaidConnectionRepository.UpdateAsync(connection);

      _logger.LogInformation("Quick sync completed for connection {ConnectionId}. Background import scheduled.", connection.Id);

      return response;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error exchanging public token with quick sync for user {UserId}", request.UserId);
      throw;
    }
  }

  public async Task<ExchangePublicTokenResponse> ExchangePublicTokenWithImportAsync(ImportHistoricalDataRequest request)
  {
    try
    {
      var exchangeRequest = new ExchangePublicTokenRequest
      {
        PublicToken = request.PublicToken,
        UserId = request.UserId
      };

      var response = await ExchangePublicTokenAsync(exchangeRequest);
      
      await ImportHistoricalDataAsync(request.UserId, response.ItemId, request.Range, 
        request.CustomStartDate, request.CustomEndDate);

      return response;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error exchanging public token with import for user {UserId}", request.UserId);
      throw;
    }
  }

  /// <summary>
  /// Plaid calls take the stored access token as a Plaid secret. A connection from another provider
  /// holds a different credential (SimpleFIN's is a URL with a password in it) that must never be
  /// sent to Plaid, so every per-connection entry point refuses it before any decrypt.
  /// </summary>
  private static void EnsurePlaid(PlaidConnection connection)
  {
    if (connection.Provider != BankProvider.Plaid)
    {
      throw new ArgumentException("This connection is not a Plaid connection");
    }
  }

  public async Task DisconnectAccountAsync(int userId, int connectionId)
  {
    try
    {
      var connection = await _plaidConnectionRepository.GetByIdAsync(connectionId);
      if (connection == null || connection.UserId != userId)
      {
        throw new ArgumentException("Connection not found or access denied");
      }
      EnsurePlaid(connection);

      // Revoke the Item at Plaid so the access token is invalidated on their side and
      // ongoing Transactions billing stops — flipping IsActive alone leaves the link live
      // at Plaid (a data-retention/compliance gap on account deletion). Best-effort: a
      // Plaid failure (e.g. the item is already gone) must not block the local disconnect.
      await TryRemovePlaidItemAsync(connection);

      // Accounts first: a failure after this point leaves an active connection with inactive
      // accounts (a retry self-heals) instead of a dead connection whose frozen balances keep
      // counting in net worth.
      await DeactivateConnectionAccountsAsync(connection.Id);
      connection.IsActive = false;
      await _plaidConnectionRepository.UpdateAsync(connection);

      _logger.LogInformation("Disconnected account connection {ConnectionId} for user {UserId}", connectionId, userId);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error disconnecting account {ConnectionId} for user {UserId}", connectionId, userId);
      throw;
    }
  }

  /// <summary>
  /// The accounts of a connection that is gone leave with it. Left active they stayed in net worth
  /// and totals at a balance that would never update again, while the Accounts page (which lists by
  /// active connection) showed none. Transactions stay. If the same account comes back on a later
  /// sync, <see cref="SyncAccountsAsync"/> reactivates it.
  /// </summary>
  private async Task DeactivateConnectionAccountsAsync(int connectionId)
  {
    foreach (var account in (await _accountRepository.GetByConnectionIdAsync(connectionId)).ToList())
    {
      account.IsActive = false;
      await _accountRepository.UpdateAsync(account);
    }
  }

  public async Task<bool> TryRemovePlaidItemAsync(PlaidConnection connection, CancellationToken cancellationToken = default)
  {
    EnsurePlaid(connection);
    try
    {
      var accessToken = _encryptionService.Decrypt(connection.AccessToken);
      if (string.IsNullOrEmpty(accessToken))
      {
        // Nothing live to revoke (already cleared) — treat as done.
        return true;
      }

      var removeResponse = await _plaidClient.ItemRemoveAsync(new ItemRemoveRequest
      {
        AccessToken = accessToken
      });
      if (removeResponse.Error != null)
      {
        // Typically ITEM_NOT_FOUND / INVALID_ACCESS_TOKEN — the Item is already gone at
        // Plaid, so it is no longer billable. Idempotent: report done so we stop retrying.
        _logger.LogWarning("Plaid ItemRemove returned {ErrorCode} for connection {ConnectionId}: {Message} (request_id: {RequestId})",
          removeResponse.Error.ErrorCode, connection.Id, removeResponse.Error.ErrorMessage, removeResponse.RequestId);
        return true;
      }

      _logger.LogInformation("Removed Plaid item for connection {ConnectionId}", connection.Id);
      return true;
    }
    catch (Exception ex)
    {
      // Transient (network / Plaid outage / decrypt hiccup). Leave the connection unmarked
      // so an unattended caller retries next run rather than locking in a still-billing Item.
      _logger.LogWarning(ex, "Failed to remove Plaid item for connection {ConnectionId}; will retry", connection.Id);
      return false;
    }
  }

  /// <summary>
  /// Starts a genuine reconnect for a broken item by returning a Plaid update-mode link token.
  /// The item only becomes Active again once the user completes Plaid Link and the fresh public
  /// token is exchanged in <see cref="HandleItemUpdateAsync"/>. This deliberately does NOT flip
  /// Status to Active or emit a "reconnected" notification up front — the stored access token is
  /// still invalid until re-auth actually succeeds, and optimistically marking the item healthy
  /// left it stuck at Active with a permanently dead token (it never re-entered LoginRequired to
  /// prompt a real re-auth).
  /// </summary>
  public async Task<string> ReconnectAccountAsync(int userId, int connectionId, string? platform = null)
  {
    try
    {
      var connection = await _plaidConnectionRepository.GetByIdAsync(connectionId);
      if (connection == null || connection.UserId != userId)
      {
        throw new ArgumentException("Connection not found or access denied");
      }
      EnsurePlaid(connection);

      // Drive Plaid update mode (re-authentication). CreateUpdateTokenAsync re-checks ownership,
      // decrypts the existing access token, and returns a link token the client opens in Plaid
      // Link update mode.
      var linkToken = await CreateUpdateTokenAsync(userId, connectionId, platform: platform);

      _logger.LogInformation(
        "Issued update-mode link token to reconnect connection {ConnectionId} for user {UserId}",
        connectionId, userId);

      return linkToken;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error reconnecting account {ConnectionId} for user {UserId}", connectionId, userId);
      throw;
    }
  }

  public async Task<AccountConnectionStatusDto> GetConnectionStatusAsync(int userId, int connectionId)
  {
    try
    {
      var connection = await _plaidConnectionRepository.GetByIdAsync(connectionId);
      if (connection == null || connection.UserId != userId)
      {
        throw new ArgumentException("Connection not found or access denied");
      }

      return new AccountConnectionStatusDto
      {
        Id = connection.Id,
        IsActive = connection.IsActive,
        LastSyncAt = connection.LastSyncAt,
        Status = connection.IsActive ? "Active" : "Disconnected",
        ErrorMessage = connection.LastError ?? connection.LastSyncError
      };
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting connection status {ConnectionId} for user {UserId}", connectionId, userId);
      throw;
    }
  }

  public async Task<(int Updated, int Failed)> BackfillWebhookUrlsAsync()
  {
    var webhookUrl = _configuration["Plaid:WebhookUrl"];
    if (string.IsNullOrWhiteSpace(webhookUrl))
    {
      throw new InvalidOperationException(
        "Plaid:WebhookUrl is not configured — set it before backfilling item webhooks.");
    }

    var connections = (await _plaidConnectionRepository.GetActiveConnectionsForSync())
      .Where(c => c.Provider == BankProvider.Plaid)
      .ToList();
    var updated = 0;
    var failed = 0;

    foreach (var connection in connections)
    {
      try
      {
        var accessToken = _encryptionService.Decrypt(connection.AccessToken);
        var response = await _plaidClient.ItemWebhookUpdateAsync(new ItemWebhookUpdateRequest
        {
          AccessToken = accessToken,
          Webhook = webhookUrl
        });

        if (response.Error is not null)
        {
          failed++;
          _logger.LogWarning(
            "Webhook backfill failed for connection {ConnectionId}: {ErrorCode} — {ErrorMessage} (request_id: {RequestId})",
            connection.Id, response.Error.ErrorCode, response.Error.ErrorMessage, response.RequestId);
        }
        else
        {
          updated++;
        }
      }
      catch (Exception ex)
      {
        failed++;
        _logger.LogWarning(ex, "Webhook backfill threw for connection {ConnectionId}", connection.Id);
      }
    }

    _logger.LogInformation(
      "Plaid webhook backfill complete: {Updated} updated, {Failed} failed of {Total} active items",
      updated, failed, connections.Count);
    return (updated, failed);
  }

  // Update Mode Methods

  public async Task<string> CreateUpdateTokenAsync(int userId, int connectionId, bool accountSelectionEnabled = false, string? platform = null)
  {
    try
    {
      var connection = await _plaidConnectionRepository.GetByIdAsync(connectionId);
      if (connection == null || connection.UserId != userId)
      {
        throw new UnauthorizedAccessException("Connection not found or access denied");
      }
      EnsurePlaid(connection);

      var accessToken = _encryptionService.Decrypt(connection.AccessToken);

      var request = new LinkTokenCreateRequest
      {
        ClientName = _clientName,
        Language = Language.English,
        CountryCodes = ConfiguredCountryCodes(),
        User = new LinkTokenCreateRequestUser
        {
          ClientUserId = userId.ToString()
        },
        AccessToken = accessToken,
        Webhook = _configuration["Plaid:WebhookUrl"]
      };

      // NEW_ACCOUNTS_AVAILABLE flow: open Link update mode with account selection so the
      // user can add the accounts Plaid reported as newly available.
      if (accountSelectionEnabled)
      {
        request.Update = new LinkTokenCreateRequestUpdate { AccountSelectionEnabled = true };
      }

      ApplyPlatformOAuth(request, platform, _configuration);

      var response = await CreateLinkTokenWithRedirectFallbackAsync(request, userId, platform);

      connection.UpdateModeUrl = $"https://cdn.plaid.com/link/v2/stable/link.html?token={response.LinkToken}";
      connection.LastStatusUpdate = DateTime.UtcNow;
      await _plaidConnectionRepository.UpdateAsync(connection);

      return response.LinkToken;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error creating update token for connection {ConnectionId}", connectionId);
      throw;
    }
  }

  public async Task<bool> HandleItemUpdateAsync(int connectionId, string publicToken)
  {
    // Checked before the try below: its catch marks the connection Error, which must never
    // happen to a connection that is not Plaid's.
    var existing = await _plaidConnectionRepository.GetByIdAsync(connectionId);
    if (existing != null)
    {
      EnsurePlaid(existing);
    }

    try
    {
      var connection = await _plaidConnectionRepository.GetByIdAsync(connectionId);
      if (connection == null)
      {
        throw new ArgumentException("Connection not found");
      }

      // Exchange the public token for a new access token
      var exchangeRequest = new ItemPublicTokenExchangeRequest
      {
        PublicToken = publicToken
      };

      var response = await _plaidClient.ItemPublicTokenExchangeAsync(exchangeRequest);

      // Update the connection with the new access token
      connection.AccessToken = _encryptionService.Encrypt(response.AccessToken);
      connection.Status = PlaidItemStatus.Active;
      connection.LastError = null;
      connection.RequiresUserAction = false;
      connection.ConsecutiveFailures = 0;
      connection.AlertSentAt = null;
      connection.UpdateModeUrl = null;
      connection.LastStatusUpdate = DateTime.UtcNow;

      await _plaidConnectionRepository.UpdateAsync(connection);

      // Mark related notifications as read
      await _notificationService.CreatePlaidReconnectedNotificationAsync(
        connection.UserId,
        connectionId,
        connection.InstitutionName ?? "your bank"
      );

      _logger.LogInformation("Successfully updated connection {ConnectionId} with new access token", connectionId);

      return true;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error handling item update for connection {ConnectionId}", connectionId);
      
      var connection = await _plaidConnectionRepository.GetByIdAsync(connectionId);
      if (connection != null)
      {
        connection.Status = PlaidItemStatus.Error;
        connection.LastError = ex.Message;
        connection.ConsecutiveFailures++;
        connection.LastStatusUpdate = DateTime.UtcNow;
        await _plaidConnectionRepository.UpdateAsync(connection);
      }

      return false;
    }
  }

  public async Task UpdateConnectionStatusAsync(int connectionId, PlaidItemStatus status, string? errorMessage = null)
  {
    try
    {
      var connection = await _plaidConnectionRepository.GetByIdAsync(connectionId);
      if (connection == null)
      {
        throw new ArgumentException("Connection not found");
      }

      connection.Status = status;
      connection.LastError = errorMessage;
      connection.LastStatusUpdate = DateTime.UtcNow;

      connection.RequiresUserAction = status switch
      {
        PlaidItemStatus.LoginRequired => true,
        PlaidItemStatus.PendingExpiration => true,
        PlaidItemStatus.Error => !string.IsNullOrEmpty(errorMessage),
        _ => false
      };

      if (status == PlaidItemStatus.Error)
      {
        connection.ConsecutiveFailures++;
      }
      else if (status == PlaidItemStatus.Active)
      {
        connection.ConsecutiveFailures = 0;
        connection.RequiresUserAction = false;
        connection.AlertSentAt = null;
        connection.UpdateModeUrl = null;
      }

      await _plaidConnectionRepository.UpdateAsync(connection);

      _logger.LogInformation("Updated connection {ConnectionId} status to {Status}", connectionId, status);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error updating connection status for {ConnectionId}", connectionId);
      throw;
    }
  }

  // Plaid error codes that mean the item needs the user to re-authenticate. Mirrors the
  // webhook classifier (PlaidWebhookService.HandleItemErrorAsync) so a failure that first
  // surfaces on the polling/sync path — before or without the ITEM/ERROR webhook — is still
  // recognized and flips the connection into LoginRequired.
  private static readonly string[] LoginRequiredErrorCodes =
  {
    "ITEM_LOGIN_REQUIRED",
    "ACCESS_NOT_GRANTED",
    "INSUFFICIENT_CREDENTIALS",
    "INVALID_CREDENTIALS",
    "INVALID_MFA",
    "ITEM_LOCKED",
    "USER_SETUP_REQUIRED",
    "MFA_NOT_SUPPORTED",
    "INVALID_SEND_METHOD",
    "NO_AUTH_ACCOUNTS",
    "USER_PERMISSION_REVOKED",
  };

  // After this many consecutive sync failures without a definitive login-required code, treat
  // the item as broken (Error) so it drops out of GetActiveConnectionsForSync and we stop
  // hammering Plaid with an invalid/failing token indefinitely.
  private const int MaxConsecutiveSyncFailures = 4;

  /// <summary>
  /// Classifies a background/polling sync failure and, when appropriate, transitions the
  /// connection out of Active so it stops being re-selected for sync — and notifies the user to
  /// reconnect. Previously the sync path only stamped LastSyncError, leaving Status=Active; a
  /// broken item (rotated credentials, expired OAuth consent) was then re-synced every 10 minutes
  /// forever with no reconnect prompt and silently stale data.
  /// </summary>
  public async Task HandleSyncFailureAsync(int connectionId, string errorMessage)
  {
    try
    {
      var connection = await _plaidConnectionRepository.GetByIdAsync(connectionId);
      if (connection == null)
      {
        return;
      }

      connection.LastSyncError = errorMessage;
      connection.LastSyncAttemptAt = DateTime.UtcNow;
      connection.ConsecutiveFailures++;

      var isLoginRequired = !string.IsNullOrEmpty(errorMessage)
        && LoginRequiredErrorCodes.Any(code => errorMessage.Contains(code, StringComparison.OrdinalIgnoreCase));

      PlaidItemStatus? newStatus = isLoginRequired
        ? PlaidItemStatus.LoginRequired
        : (connection.ConsecutiveFailures >= MaxConsecutiveSyncFailures ? PlaidItemStatus.Error : null);

      if (newStatus == null)
      {
        // Transient failure below the threshold: record it and let the next cycle retry.
        await _plaidConnectionRepository.UpdateAsync(connection);
        _logger.LogWarning(
          "Sync failure #{Failures} for connection {ConnectionId} (still retrying): {Error}",
          connection.ConsecutiveFailures, connectionId, errorMessage);
        return;
      }

      connection.Status = newStatus.Value;
      connection.LastError = errorMessage;
      connection.RequiresUserAction = true;
      connection.LastStatusUpdate = DateTime.UtcNow;
      await _plaidConnectionRepository.UpdateAsync(connection);

      // Prompt the user to reconnect (same notification the webhook error path raises). The
      // connection is now non-Active, so GetActiveConnectionsForSync excludes it and this fires
      // once rather than every cycle.
      await _alerts.NotifyNeedsAttentionAsync(
        connection,
        isLoginRequired ? "ITEM_LOGIN_REQUIRED" : "SYNC_FAILED");

      _logger.LogWarning(
        "Connection {ConnectionId} transitioned to {Status} after {Failures} consecutive sync failures: {Error}",
        connectionId, newStatus.Value, connection.ConsecutiveFailures, errorMessage);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to record sync failure for connection {ConnectionId}", connectionId);
    }
  }

  public async Task<PlaidConnection?> GetConnectionByItemIdAsync(string itemId)
  {
    try
    {
      return await _plaidConnectionRepository.GetByItemIdAsync(itemId);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting connection by item ID {ItemId}", itemId);
      throw;
    }
  }

  public async Task HandlePendingExpirationAsync(int connectionId, DateTime expirationDate)
  {
    try
    {
      var connection = await _plaidConnectionRepository.GetByIdAsync(connectionId);
      if (connection == null)
      {
        throw new ArgumentException("Connection not found");
      }

      connection.Status = PlaidItemStatus.PendingExpiration;
      connection.ExpirationDate = expirationDate;
      connection.RequiresUserAction = true;
      connection.LastStatusUpdate = DateTime.UtcNow;

      await _plaidConnectionRepository.UpdateAsync(connection);

      // The connection now needs the user within days; tell them now, not when it breaks.
      await _alerts.NotifyNeedsAttentionAsync(connection, "PENDING_EXPIRATION");

      _logger.LogInformation("Set connection {ConnectionId} to pending expiration on {ExpirationDate}", 
        connectionId, expirationDate);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error handling pending expiration for connection {ConnectionId}", connectionId);
      throw;
    }
  }

  public async Task HandleUserPermissionRevokedAsync(int connectionId)
  {
    try
    {
      var connection = await _plaidConnectionRepository.GetByIdAsync(connectionId);
      if (connection == null)
      {
        throw new ArgumentException("Connection not found");
      }

      connection.Status = PlaidItemStatus.Disconnected;
      connection.IsActive = false;
      connection.DisconnectDate = DateTime.UtcNow;
      connection.RequiresUserAction = false;
      connection.LastStatusUpdate = DateTime.UtcNow;

      await DeactivateConnectionAccountsAsync(connection.Id);
      await _plaidConnectionRepository.UpdateAsync(connection);

      _logger.LogInformation("Marked connection {ConnectionId} as disconnected due to revoked permissions", 
        connectionId);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error handling user permission revoked for connection {ConnectionId}", connectionId);
      throw;
    }
  }

}
