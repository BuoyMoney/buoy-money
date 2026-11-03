using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services.Oidc;

/// <summary>
/// The server side of single sign-on (self-host). Three steps:
/// <list type="number">
/// <item><see cref="StartAsync"/>: remember a state, nonce and PKCE verifier, send the browser to the provider.</item>
/// <item><see cref="CallbackAsync"/>: the provider sends the browser back; trade the code, verify the ID
/// token, find or create the account, and send the browser to the app with a one-time code.</item>
/// <item><see cref="ExchangeAsync"/>: the app trades that code (once, within 60 seconds) for its user.
/// The controller then runs the normal MFA check and issues the usual tokens.</item>
/// </list>
/// State lives in the database (<c>oidc_login_requests</c>), not in memory, so a sign-in survives a
/// restart and the hosted blue-green pair (where this is off anyway) could never split a round trip.
/// </summary>
public partial class OidcLoginService
{
  /// <summary>The app's own URL scheme. The provider sends a phone back to
  /// <c>com.buoyai.buoymoney://oidc?code=...</c>; Android and iOS register it.</summary>
  public const string MobileScheme = "com.buoyai.buoymoney";

  public const string PlatformWeb = "web";
  public const string PlatformMobile = "mobile";

  public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
  public static readonly TimeSpan ExchangeLifetime = TimeSpan.FromSeconds(60);

  private readonly BuoyAIDbContext _db;
  private readonly OidcProviderClient _provider;
  private readonly IAuthService _auth;
  private readonly IConfiguration _configuration;
  private readonly ILogger<OidcLoginService> _logger;

  public OidcLoginService(
    BuoyAIDbContext db,
    OidcProviderClient provider,
    IAuthService auth,
    IConfiguration configuration,
    ILogger<OidcLoginService> logger)
  {
    _db = db;
    _provider = provider;
    _auth = auth;
    _configuration = configuration;
    _logger = logger;
  }

  public OidcOptions Options => OidcOptions.From(_configuration);

  /// <summary>Where the provider sends the browser back. Register exactly this at the provider.</summary>
  public string CallbackUri(string requestOrigin)
  {
    var options = Options;
    if (options.RedirectUri != null) return options.RedirectUri;
    return $"{options.PublicOrigin ?? requestOrigin.TrimEnd('/')}/api/auth/oidc/callback";
  }

  /// <summary>Builds the provider's authorize URL for a new sign-in and records its state.</summary>
  public async Task<string> StartAsync(
    string? platform, string? returnPath, string? inviteCode, string? clientChallenge, string requestOrigin,
    CancellationToken ct = default)
  {
    var options = Options;
    platform = NormalizePlatform(platform)
      ?? throw new OidcLoginException("invalid_request", "Unknown platform.");
    if (clientChallenge != null && !Base64UrlChallenge().IsMatch(clientChallenge))
      throw new OidcLoginException("invalid_request", "The app sent a malformed code challenge.");
    // A deep link can be caught by another app that claims the same scheme. The app's own PKCE pair
    // makes a caught code useless to it, so mobile must send one.
    if (platform == PlatformMobile && clientChallenge == null)
      throw new OidcLoginException("invalid_request", "Update the app to sign in with single sign-on.");

    await DeleteExpiredAsync(ct);

    var state = RandomToken();
    var nonce = RandomToken();
    var verifier = RandomToken();
    _db.OidcLoginRequests.Add(new OidcLoginRequest
    {
      Id = Guid.NewGuid(),
      Kind = OidcLoginRequest.KindState,
      SecretHash = Hash(state),
      Platform = platform,
      Nonce = nonce,
      CodeVerifier = verifier,
      ReturnPath = SafeReturnPath(returnPath),
      InviteCode = string.IsNullOrWhiteSpace(inviteCode) ? null : inviteCode.Trim()[..Math.Min(inviteCode.Trim().Length, 64)],
      ClientChallenge = clientChallenge,
      CreatedAt = DateTime.UtcNow,
      ExpiresAt = DateTime.UtcNow.Add(StateLifetime),
    });
    await _db.SaveChangesAsync(ct);

    return await _provider.BuildAuthorizationUrlAsync(options, CallbackUri(requestOrigin), state, nonce, S256(verifier), ct);
  }

  /// <summary>
  /// Handles the provider's redirect back and returns where to send the browser next: the app with
  /// <c>?code=</c> on success, or with <c>?error=</c> (a short code) on any failure. Never throws for
  /// a bad sign-in.
  /// </summary>
  public async Task<string> CallbackAsync(
    string? state, string? code, string? providerError, string requestOrigin, CancellationToken ct = default)
  {
    OidcLoginRequest? row = null;
    if (!string.IsNullOrEmpty(state))
    {
      var hash = Hash(state);
      row = await _db.OidcLoginRequests
        .FirstOrDefaultAsync(r => r.SecretHash == hash && r.Kind == OidcLoginRequest.KindState, ct);
    }
    if (row == null || row.ExpiresAt <= DateTime.UtcNow)
    {
      return ClientRedirect(row?.Platform ?? PlatformWeb, requestOrigin, null, "expired", null);
    }

    // Spend the state before talking to the provider: a replayed callback finds nothing.
    if (!await TryDeleteAsync(row, ct))
      return ClientRedirect(row.Platform, requestOrigin, null, "expired", null);

    if (!string.IsNullOrEmpty(providerError) || string.IsNullOrEmpty(code))
    {
      _logger.LogInformation("Single sign-on ended at the provider: {Error}", providerError ?? "no code");
      return ClientRedirect(row.Platform, requestOrigin, null, "denied", null);
    }

    try
    {
      var options = Options;
      var identity = await _provider.RedeemCodeAsync(
        options, CallbackUri(requestOrigin), code, row.CodeVerifier!, row.Nonce!, ct);

      if (!identity.EmailVerified && options.TrustUnverifiedEmail && !string.IsNullOrWhiteSpace(identity.Email))
        identity = identity with { EmailVerified = true };

      var resolved = await _auth.ResolveOidcUserAsync(identity, options.AutoRegister, row.InviteCode);

      var exchangeCode = RandomToken();
      _db.OidcLoginRequests.Add(new OidcLoginRequest
      {
        Id = Guid.NewGuid(),
        Kind = OidcLoginRequest.KindExchange,
        SecretHash = Hash(exchangeCode),
        Platform = row.Platform,
        ReturnPath = row.ReturnPath,
        ClientChallenge = row.ClientChallenge,
        UserId = resolved.UserId,
        IsNewUser = resolved.IsNewUser,
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.Add(ExchangeLifetime),
      });
      await _db.SaveChangesAsync(ct);
      return ClientRedirect(row.Platform, requestOrigin, exchangeCode, null, row.ReturnPath);
    }
    catch (OidcLoginException ex)
    {
      _logger.LogInformation("Single sign-on refused: {Code} {Message}", ex.Code, ex.Message);
      return ClientRedirect(row.Platform, requestOrigin, null, ex.Code, null);
    }
  }

  /// <summary>
  /// Trades the one-time code for the user it names. Single use and 60 seconds; when the app sent a
  /// challenge to start, the matching verifier is required.
  /// </summary>
  public async Task<OidcResolvedUser> ExchangeAsync(string? code, string? codeVerifier, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(code))
      throw new OidcLoginException("expired", "This sign-in link has expired. Please try again.");

    var hash = Hash(code.Trim());
    var row = await _db.OidcLoginRequests
      .FirstOrDefaultAsync(r => r.SecretHash == hash && r.Kind == OidcLoginRequest.KindExchange, ct);
    if (row == null || row.UserId == null)
      throw new OidcLoginException("expired", "This sign-in link has expired. Please try again.");

    // Delete first: whatever happens next, this code never works twice.
    var spent = await TryDeleteAsync(row, ct);
    if (!spent || row.ExpiresAt <= DateTime.UtcNow)
      throw new OidcLoginException("expired", "This sign-in link has expired. Please try again.");

    if (row.ClientChallenge != null &&
        (string.IsNullOrEmpty(codeVerifier) || !FixedTimeEquals(S256(codeVerifier), row.ClientChallenge)))
    {
      _logger.LogWarning("Single sign-on exchange with a wrong app verifier");
      throw new OidcLoginException("expired", "This sign-in link has expired. Please try again.");
    }

    return new OidcResolvedUser(row.UserId.Value, row.IsNewUser);
  }

  /// <summary>Where the browser goes after the provider: the web app route or the phone app's deep link.</summary>
  public string ClientRedirect(string platform, string requestOrigin, string? code, string? error, string? returnPath)
  {
    var query = new List<string>();
    if (code != null) query.Add($"code={Uri.EscapeDataString(code)}");
    if (error != null) query.Add($"error={Uri.EscapeDataString(error)}");
    if (returnPath != null) query.Add($"next={Uri.EscapeDataString(returnPath)}");
    var qs = string.Join("&", query);
    if (platform == PlatformMobile) return $"{MobileScheme}://oidc?{qs}";
    var origin = Options.PublicOrigin ?? requestOrigin.TrimEnd('/');
    return $"{origin}/app/auth/oidc?{qs}";
  }

  public static string? NormalizePlatform(string? platform) => platform?.Trim().ToLowerInvariant() switch
  {
    null or "" or PlatformWeb => PlatformWeb,
    PlatformMobile => PlatformMobile,
    _ => null,
  };

  /// <summary>Only a path inside the app ("/budgets"), never another site ("//evil", "https://...").</summary>
  internal static string? SafeReturnPath(string? path)
  {
    if (string.IsNullOrWhiteSpace(path)) return null;
    path = path.Trim();
    if (path.Length > 200 || !path.StartsWith('/') || path.StartsWith("//") || path.Contains('\\') || path.Contains(':'))
      return null;
    return path;
  }

  private async Task<bool> TryDeleteAsync(OidcLoginRequest row, CancellationToken ct)
  {
    _db.OidcLoginRequests.Remove(row);
    try
    {
      await _db.SaveChangesAsync(ct);
      return true;
    }
    catch (DbUpdateConcurrencyException)
    {
      // Another request deleted it first.
      _db.Entry(row).State = EntityState.Detached;
      return false;
    }
  }

  private async Task DeleteExpiredAsync(CancellationToken ct)
  {
    var cutoff = DateTime.UtcNow;
    var stale = await _db.OidcLoginRequests.Where(r => r.ExpiresAt < cutoff).Take(200).ToListAsync(ct);
    if (stale.Count == 0) return;
    _db.OidcLoginRequests.RemoveRange(stale);
    try
    {
      await _db.SaveChangesAsync(ct);
    }
    catch (DbUpdateConcurrencyException)
    {
      foreach (var r in stale) _db.Entry(r).State = EntityState.Detached;
    }
  }

  private static string RandomToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

  internal static string S256(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

  private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

  private static string Base64Url(byte[] bytes) =>
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

  private static bool FixedTimeEquals(string a, string b) =>
    CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

  [GeneratedRegex("^[A-Za-z0-9_-]{43,128}$")]
  private static partial Regex Base64UrlChallenge();
}
