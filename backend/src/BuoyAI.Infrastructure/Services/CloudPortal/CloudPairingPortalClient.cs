using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuoyAI.Infrastructure.Services.CloudPortal;

/// <summary>
/// <see cref="ICloudPairingPortal"/> over HTTP to the Cloud bridge's <c>/v1/pairing</c> owner endpoints.
/// The owner token goes in the Authorization header only; it and the key are never logged. Link codes ride in the
/// path, so every logged path goes through <see cref="SecretPathRedaction"/> first.
/// </summary>
public sealed class CloudPairingPortalClient : ICloudPairingPortal
{
    public const string HttpClientName = "cloud-portal";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _factory;
    private readonly CloudPortalOptions _options;
    private readonly CloudOwnerTokenIssuer _tokens;
    private readonly ILogger<CloudPairingPortalClient> _logger;

    public CloudPairingPortalClient(
        IHttpClientFactory factory,
        IOptions<CloudPortalOptions> options,
        CloudOwnerTokenIssuer tokens,
        ILogger<CloudPairingPortalClient> logger)
    {
        _factory = factory;
        _options = options.Value;
        _tokens = tokens;
        _logger = logger;
    }

    public bool IsConfigured => _options.IsConfigured;

    public async Task<IReadOnlyList<CloudServer>> ListServersAsync(string ownerId, CancellationToken ct = default)
    {
        var rows = await SendAsync<List<BridgeServer>>(HttpMethod.Get, "v1/pairing/servers", ownerId, mfa: false, body: null, ct);
        return (rows ?? []).Select(ToServer).OrderByDescending(s => s.PairedAt).ToList();
    }

    public async Task<CloudPairingCode> CreateCodeAsync(string ownerId, bool mfa, bool autoConfirm = false, CancellationToken ct = default)
    {
        object body = autoConfirm ? new { autoConfirm = true } : new { };
        var code = await SendAsync<BridgeCode>(HttpMethod.Post, "v1/pairing/codes", ownerId, mfa, body, ct);
        if (code == null || string.IsNullOrEmpty(code.Code))
        {
            throw Unavailable();
        }
        return new CloudPairingCode(code.Code, Utc(code.ExpiresAt));
    }

    public Task ConfirmAsync(string ownerId, Guid serverId, bool mfa, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Post, $"v1/pairing/servers/{serverId}/confirm", ownerId, mfa, new { }, ct);

    public Task RevokeAsync(string ownerId, Guid serverId, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, $"v1/pairing/servers/{serverId}", ownerId, mfa: false, body: null, ct);

    public Task<CloudLinkSession> GetLinkSessionAsync(string ownerId, string userCode, CancellationToken ct = default) =>
        LinkSessionCallAsync(HttpMethod.Get, LinkSessionPath(userCode), ownerId, userCode, mfa: false, body: null, ct);

    public Task<CloudLinkSession> ApproveLinkSessionAsync(string ownerId, string userCode, bool mfa, CancellationToken ct = default) =>
        LinkSessionCallAsync(HttpMethod.Post, LinkSessionPath(userCode) + "/approve", ownerId, userCode, mfa, new { }, ct);

    public Task<CloudLinkSession> DenyLinkSessionAsync(string ownerId, string userCode, CancellationToken ct = default) =>
        LinkSessionCallAsync(HttpMethod.Post, LinkSessionPath(userCode) + "/deny", ownerId, userCode, mfa: false, new { }, ct);

    /// <summary>GET, approve and deny all answer with the session as it stands.</summary>
    private async Task<CloudLinkSession> LinkSessionCallAsync(
        HttpMethod method, string path, string ownerId, string userCode, bool mfa, object? body, CancellationToken ct)
    {
        var row = await SendAsync<BridgeLinkSession>(method, path, ownerId, mfa, body, ct, notFound: LinkCodeNotFound);
        if (row == null) throw Unavailable();
        return new CloudLinkSession(userCode, row.Name ?? "", string.IsNullOrWhiteSpace(row.ServerVersion) ? null : row.ServerVersion,
            Utc(row.CreatedAt), Utc(row.ExpiresAt), string.IsNullOrEmpty(row.Status) ? "pending" : row.Status);
    }

    private static string LinkSessionPath(string userCode) => "v1/pairing/link-sessions/" + Uri.EscapeDataString(userCode);

    private static CloudPortalException LinkCodeNotFound() =>
        new("invalid_code", "That code is not valid or has expired. Check the code on your server.", 404);

    internal static CloudServer ToServer(BridgeServer s)
    {
        var status = s.RevokedAt != null ? "revoked" : s.ConfirmedAt == null ? "awaiting_confirmation" : "linked";
        return new CloudServer(s.Id, s.Name ?? "", s.ServerVersion, Utc(s.CreatedAt), Utc(s.ConfirmedAt), Utc(s.LastSeenAt), Utc(s.RevokedAt), status);
    }

    private static DateTime Utc(DateTime d) => d.Kind == DateTimeKind.Utc ? d : d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc);
    private static DateTime? Utc(DateTime? d) => d == null ? null : Utc(d.Value);

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, string ownerId, bool mfa, object? body, CancellationToken ct,
        Func<CloudPortalException>? notFound = null)
        where T : class
    {
        if (!_options.IsConfigured)
        {
            throw new CloudPortalException("not_available", "Linking servers is not available here.", 404);
        }

        var http = _factory.CreateClient(HttpClientName);
        http.BaseAddress = new Uri(_options.PairingUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds));

        var logPath = SecretPathRedaction.Redact(path);
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens.Mint(ownerId, mfa));
        if (body != null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Cloud pairing API unreachable ({Method} {Path}): {Error}", method, logPath, ex.Message);
            throw Unavailable();
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Cloud pairing API timed out ({Method} {Path})", method, logPath);
            throw Unavailable();
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object)) return null;
                return await response.Content.ReadFromJsonAsync<T>(Json, ct);
            }

            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    throw notFound?.Invoke() ?? new CloudPortalException("not_found", "That server is not on your account.", 404);
                case HttpStatusCode.Gone:
                case HttpStatusCode.Conflict:
                    // A link session another owner (or a deny) already decided, or one that has run out.
                    var (closedCode, _) = await ReadErrorAsync(response, ct);
                    throw new CloudPortalException(closedCode ?? "already_decided",
                        "This code was already used. Start again on your server to get a new one.", (int)response.StatusCode);
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    // Our token was refused: the owner key or issuer differs from the bridge's. A setup problem, not the user's.
                    _logger.LogError(
                        "Cloud pairing API refused the owner token ({Status}) on {Method} {Path}. Check that CloudPortal:OwnerJwtKey equals the bridge's Owner:JwtKey.",
                        (int)response.StatusCode, method, logPath);
                    throw new CloudPortalException("cloud_auth_failed", "BuoyAI Cloud did not accept this request. Try again later.", 502);
                case HttpStatusCode.TooManyRequests:
                    throw new CloudPortalException("rate_limited", "Too many tries. Wait a bit and try again.", 429);
                case HttpStatusCode.BadRequest:
                    var (code, message) = await ReadErrorAsync(response, ct);
                    throw code switch
                    {
                        "too_many_codes" => new CloudPortalException(code, message ?? "Too many unused pairing codes. Wait for one to expire.", 429),
                        "unknown_server" => new CloudPortalException(code, message ?? "That server is not on your account.", 404),
                        _ => new CloudPortalException(code ?? "cloud_refused", message ?? "BuoyAI Cloud refused the request.", 400),
                    };
                default:
                    _logger.LogWarning("Cloud pairing API answered {Status} on {Method} {Path}", (int)response.StatusCode, method, logPath);
                    throw Unavailable();
            }
        }
    }

    private static CloudPortalException Unavailable() =>
        new("cloud_unavailable", "BuoyAI Cloud is not reachable right now. Try again later.", 502);

    private static async Task<(string? Code, string? Message)> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            string? Get(string name) =>
                root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;
            return (Get("error") ?? Get("code"), Get("message"));
        }
        catch (JsonException)
        {
            // Not the bridge's JSON (a proxy page): never echo the body.
            return (null, null);
        }
    }

    internal sealed record BridgeServer(
        Guid Id,
        string? Name,
        string? ServerVersion,
        DateTime CreatedAt,
        DateTime? ConfirmedAt,
        DateTime? LastSeenAt,
        DateTime? RevokedAt,
        DateTime? DeliveryResumesAt);

    internal sealed record BridgeCode(string Code, DateTime ExpiresAt);

    internal sealed record BridgeLinkSession(
        string? Name,
        string? ServerVersion,
        DateTime CreatedAt,
        DateTime ExpiresAt,
        string? Status);
}
