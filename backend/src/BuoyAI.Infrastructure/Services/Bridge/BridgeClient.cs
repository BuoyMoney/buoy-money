using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.DTOs.Billing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuoyAI.Infrastructure.Services.Bridge;

/// <summary>A refusal from the Cloud, with a reason a caller can act on and text safe to show.</summary>
public class BridgeCloudException(int status, string error, string message, TimeSpan? retryAfter = null) : Exception(message)
{
  /// <summary>How long the Cloud asked this server to wait, when it said (the Retry-After header).</summary>
  public TimeSpan? RetryAfter { get; } = retryAfter;

  /// <summary>
  /// A throttle or a not-ready answer, not a fault: the Cloud's rate or daily cap, the bank still loading its first
  /// transactions, or a pause. Nothing is wrong with the connection, so the caller waits and tries again later.
  /// </summary>
  public bool RetryLater => Status == 429 || Error is "rate_limited" or "daily_cap" or "plaid_rate_limited" or "not_ready" or "paused";

  public int Status { get; } = status;

  /// <summary>Machine-readable reason: login_required, upgrade_required, cap_reached, rate_limited, paused, ...</summary>
  public string Error { get; } = error;

  /// <summary>The Cloud no longer accepts this server's credential (revoked, or re-paired elsewhere).</summary>
  public bool CredentialRejected => Status == 401;

  public bool NeedsUserAtBank => Error == "login_required";
}

public record BridgeItem(Guid Id, string? InstitutionName, string Status, bool UpdatesAvailable, bool InitialUpdateComplete,
  bool HistoricalUpdateComplete, DateTime CreatedAt, DateTime? LastDeliveredAt, bool OverCap, string? Notice = null, DateTime? NoticeAt = null);

public record BridgeLink(string SessionId, string Url, DateTime ExpiresAt);

/// <summary>Where one link session stands at the Cloud: pending, finished (with the bank it made), failed or expired.</summary>
public record BridgeLinkResult(string Status, Guid? ItemId);

public record BridgeBatch(Guid BatchId, Guid ItemId, string CursorAfter, bool HasMore, int Added, int Modified, int Removed, string Sealed);

/// <summary>
/// Talks to the BuoyAI Cloud bridge as this server: Bearer credential plus an ECDSA signature over every request
/// (see the Cloud Pairing service. The access token for the bank never reaches this server, only batches
/// sealed to this server's key.
/// </summary>
public class BridgeClient
{
  private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
  private readonly IHttpClientFactory _http;
  private readonly CloudPairingService _pairing;
  private readonly CloudBridgeOptions _options;
  private readonly TimeProvider _time;
  private readonly ILogger<BridgeClient> _logger;

  public BridgeClient(IHttpClientFactory http, CloudPairingService pairing, IOptions<CloudBridgeOptions> options,
    TimeProvider time, ILogger<BridgeClient> logger)
  {
    _http = http;
    _pairing = pairing;
    _options = options.Value;
    _time = time;
    _logger = logger;
  }

  /// <summary>True when this server is paired and the Cloud address is usable.</summary>
  public async Task<bool> IsPairedAsync(CancellationToken ct = default)
  {
    using var identity = await _pairing.GetIdentityAsync(ct);
    return identity != null;
  }

  public async Task<IReadOnlyList<BridgeItem>> ListItemsAsync(CancellationToken ct = default) =>
    await CallAsync<List<BridgeItem>>(HttpMethod.Get, "bridge/v1/items", null, ct) ?? [];

  /// <summary>Starts the hosted page. With <paramref name="reconnectItemId"/> the person signs in again for that bank instead.</summary>
  public Task<BridgeLink?> StartLinkAsync(Guid? reconnectItemId = null, CancellationToken ct = default) =>
    CallAsync<BridgeLink>(HttpMethod.Post, "bridge/v1/link/start", reconnectItemId == null ? null : new { itemId = reconnectItemId }, ct);

  /// <summary>Which bank a link session made. Only the server that started the session can ask.</summary>
  public Task<BridgeLinkResult?> GetLinkResultAsync(string sessionId, CancellationToken ct = default) =>
    CallAsync<BridgeLinkResult>(HttpMethod.Get, $"bridge/v1/link/{Uri.EscapeDataString(sessionId)}/result", null, ct);

  /// <summary>One page for one bank, or null when the Cloud has nothing new.</summary>
  public Task<BridgeBatch?> PullAsync(Guid itemId, CancellationToken ct = default) =>
    CallAsync<BridgeBatch>(HttpMethod.Post, $"bridge/v1/items/{itemId:D}/pull", null, ct);

  public Task AckAsync(Guid batchId, CancellationToken ct = default) =>
    CallAsync<JsonNode>(HttpMethod.Post, $"bridge/v1/batches/{batchId:D}/ack", null, ct);

  /// <summary>
  /// This server's Cloud plan and next invoice, as the Cloud's billing service reports them for the account this server is
  /// paired to. Null when the Cloud has no billing set up (404). Read only.
  /// </summary>
  public Task<CloudBillingSummary?> GetBillingAsync(CancellationToken ct = default) =>
    CallAsync<CloudBillingSummary>(HttpMethod.Get, "bridge/v1/billing", null, ct);

  /// <summary>
  /// A Stripe customer portal address for the paired account's card, billing details and invoices. <paramref name="returnUrl"/>
  /// must be https; the Cloud falls back to its own page when it is missing.
  /// </summary>
  public async Task<string?> CreateBillingPortalAsync(string? returnUrl, CancellationToken ct = default) =>
    (await CallAsync<CloudPortalSession>(HttpMethod.Post, "bridge/v1/billing/portal-session", new { returnUrl }, ct))?.Url;

  /// <summary>Removes the bank at Plaid through the Cloud, which ends its monthly charge.</summary>
  public Task RemoveItemAsync(Guid itemId, CancellationToken ct = default) =>
    CallAsync<JsonNode>(HttpMethod.Delete, $"bridge/v1/items/{itemId:D}", null, ct);

  /// <summary>Decrypts a batch with this server's key. Throws <see cref="CryptographicException"/> for a batch that is not ours.</summary>
  public async Task<JsonObject> OpenBatchAsync(BridgeBatch batch, CancellationToken ct = default)
  {
    using var identity = await _pairing.GetIdentityAsync(ct) ?? throw new BridgeCloudException(401, "not_paired", "This server is not paired with the Cloud.");
    var aad = BridgeHpke.FeedAad(identity.ServerId, batch.BatchId, batch.ItemId, batch.CursorAfter);
    var plain = BridgeHpke.Open(identity.EncryptionKey, FromBase64Url(batch.Sealed), BridgeHpke.FeedInfo, aad);
    try
    {
      var body = JsonNode.Parse(plain)?.AsObject() ?? throw new CryptographicException("Empty batch.");
      // The batch names itself; it must be the one we asked for, or a mixed-up reply could be applied to the wrong bank.
      if (body["batch_id"]?.GetValue<Guid>() != batch.BatchId || body["item_id"]?.GetValue<Guid>() != batch.ItemId)
        throw new CryptographicException("Batch does not match its envelope.");
      return body;
    }
    finally
    {
      CryptographicOperations.ZeroMemory(plain);
    }
  }

  private async Task<T?> CallAsync<T>(HttpMethod method, string relativePath, object? body, CancellationToken ct)
  {
    using var identity = await _pairing.GetIdentityAsync(ct)
                         ?? throw new BridgeCloudException(401, "not_paired", "This server is not paired with the Cloud.");
    var uri = new Uri(identity.CloudUrl, relativePath);
    var bytes = body == null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, Json);
    var timestamp = _time.GetUtcNow().ToUnixTimeSeconds();

    using var request = new HttpRequestMessage(method, uri);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Credential);
    request.Headers.Add("X-Buoy-Timestamp", timestamp.ToString());
    request.Headers.Add("X-Buoy-Signature", Sign(identity.SigningKey, method.Method, uri.PathAndQuery, bytes, timestamp));
    request.Headers.Add("X-Buoy-Server-Version", CloudPairingService.ServerVersion(_options));
    if (bytes.Length > 0)
    {
      request.Content = new ByteArrayContent(bytes);
      request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    }

    var client = _http.CreateClient(CloudPairingService.HttpClientName);
    HttpResponseMessage response;
    try
    {
      response = await client.SendAsync(request, ct);
    }
    catch (HttpRequestException ex)
    {
      _logger.LogWarning("Cloud bridge request failed: {Kind}", ex.GetType().Name);
      throw new BridgeCloudException(503, "unreachable", "Could not reach the BuoyAI Cloud.");
    }

    using (response)
    {
      if (response.StatusCode == HttpStatusCode.NoContent) return default;
      var text = await response.Content.ReadAsStringAsync(ct);
      if (!response.IsSuccessStatusCode) throw Refusal(response.StatusCode, text, response.Headers.RetryAfter?.Delta);
      return string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<T>(text, Json);
    }
  }

  private static BridgeCloudException Refusal(HttpStatusCode status, string body, TimeSpan? retryAfter)
  {
    string error = status == HttpStatusCode.Unauthorized ? "credential_rejected" : "error";
    string message = status switch
    {
      HttpStatusCode.Unauthorized => "The Cloud no longer accepts this server. Pair it again from your Cloud account.",
      (HttpStatusCode)426 => "This version of Buoy Money is too old for the Cloud bank feed. Please update.",
      HttpStatusCode.TooManyRequests => "The Cloud asked this server to slow down.",
      _ => "The Cloud could not complete that request."
    };
    try
    {
      var json = JsonNode.Parse(body);
      error = json?["error"]?.GetValue<string>() ?? error;
      // Cloud messages are written for people and never carry bank data.
      if (json?["message"]?.GetValue<string>() is { Length: > 0 and <= 300 } m) message = m;
    }
    catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
    return new BridgeCloudException((int)status, error, message, retryAfter);
  }

  private static string Sign(ECDsa key, string method, string pathAndQuery, byte[] body, long timestamp)
  {
    var canonical = $"{method.ToUpperInvariant()}\n{pathAndQuery}\n{Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant()}\n{timestamp}";
    return Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256));
  }

  public static byte[] FromBase64Url(string s)
  {
    var t = s.Replace('-', '+').Replace('_', '/');
    t = t.PadRight(t.Length + (4 - t.Length % 4) % 4, '=');
    return Convert.FromBase64String(t);
  }
}
