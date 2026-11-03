using System.Net;
using System.Net.Http.Json;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services.Bridge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>A phone paired with the push relay: the relay's pairing credential and the phone's public key.</summary>
public record RelayDevice(string PairingToken, string PublicKey);

/// <summary>
/// Sends push through the BuoyAI push relay so a self-hosted server needs no Firebase or Apple
/// credentials. Each message is encrypted to one phone (<see cref="PushEnvelope"/>) before it
/// leaves this server. Off unless <c>PushRelay:Url</c> is set. Contract: docs/self_host/push_relay.md.
/// </summary>
public interface IPushRelaySender
{
  bool IsEnabled { get; }

  /// <summary>
  /// True when pushes can actually leave: the relay address is set and this server has a credential the relay accepts
  /// (<c>PushRelay:ServerKey</c>, or its BuoyAI Cloud link when the relay is the Cloud). A phone paired with the relay is
  /// not enough on its own: the relay refuses a push from a server it cannot identify.
  /// </summary>
  Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);

  /// <summary>Returns the pairing tokens the relay says are gone (404 or 410), so the caller can drop them.</summary>
  Task<FcmSendResult> SendAsync(IReadOnlyList<RelayDevice> devices, string title, string body,
    IReadOnlyDictionary<string, string> data, CancellationToken cancellationToken = default);
}

public class PushRelaySender : IPushRelaySender
{
  public const string HttpClientName = "push-relay";

  private readonly IHttpClientFactory _httpClientFactory;
  private readonly ILogger<PushRelaySender> _logger;
  private readonly Uri? _pushUri;
  private readonly string? _configuredKey;
  private readonly IServiceScopeFactory? _scopes;

  /// <param name="scopes">
  /// Lets the sender read this server's Cloud pairing (the relay wants the per-server credential). Without it, and
  /// without <c>PushRelay:ServerKey</c>, requests carry no credential, as before.
  /// </param>
  public PushRelaySender(IConfiguration configuration, IHttpClientFactory httpClientFactory, ILogger<PushRelaySender> logger,
    IServiceScopeFactory? scopes = null)
  {
    _httpClientFactory = httpClientFactory;
    _logger = logger;
    _scopes = scopes;
    _configuredKey = configuration["PushRelay:ServerKey"];
    var url = configuration["PushRelay:Url"];
    if (!string.IsNullOrWhiteSpace(url))
    {
      if (Uri.TryCreate(url.Trim().TrimEnd('/') + "/v1/push", UriKind.Absolute, out var uri)
          && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
      {
        _pushUri = uri;
      }
      else
      {
        _logger.LogWarning("PushRelay:Url is not a valid http(s) address; relay push is off");
      }
    }
  }

  public bool IsEnabled => _pushUri != null;

  public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) =>
    _pushUri != null && await ResolveCredentialAsync(cancellationToken) != null;

  public async Task<FcmSendResult> SendAsync(IReadOnlyList<RelayDevice> devices, string title, string body,
    IReadOnlyDictionary<string, string> data, CancellationToken cancellationToken = default)
  {
    if (_pushUri == null || devices.Count == 0) return FcmSendResult.Empty;

    var client = _httpClientFactory.CreateClient(HttpClientName);
    client.Timeout = TimeSpan.FromSeconds(15);
    var credential = await ResolveCredentialAsync(cancellationToken);
    if (credential != null) client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
    var success = 0;
    var failure = 0;
    var gone = new List<string>();

    foreach (var device in devices)
    {
      try
      {
        var envelope = PushEnvelope.Seal(device.PublicKey, title, body, data);
        using var response = await client.PostAsJsonAsync(_pushUri,
          new { pairingToken = device.PairingToken, envelope, ttl = 86400, priority = "high" }, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
          success++;
        }
        else if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
          failure++;
          gone.Add(device.PairingToken);
        }
        else
        {
          failure++;
          _logger.LogWarning("Push relay answered {Status} for one device", (int)response.StatusCode);
        }
      }
      catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException or FormatException)
      {
        if (cancellationToken.IsCancellationRequested) throw;
        failure++;
        _logger.LogWarning(ex, "Push relay send failed for one device");
      }
    }

    return new FcmSendResult(success, failure, gone);
  }

  /// <summary>True when the relay is the paired Cloud itself: same host over https, or both on this machine.</summary>
  internal static bool IsSameCloudHost(Uri cloud, Uri relay)
  {
    if (!string.Equals(cloud.Host, relay.Host, StringComparison.OrdinalIgnoreCase)) return false;
    if (relay.Scheme == Uri.UriSchemeHttps) return true;
    return relay.Scheme == Uri.UriSchemeHttp && relay.IsLoopback;
  }

  /// <summary>The per-server credential the relay checks: the setting if there is one, else the stored Cloud pairing.</summary>
  private async Task<string?> ResolveCredentialAsync(CancellationToken ct)
  {
    if (!string.IsNullOrWhiteSpace(_configuredKey)) return _configuredKey.Trim();
    if (_scopes == null) return null;
    try
    {
      using var scope = _scopes.CreateScope();
      var pairing = scope.ServiceProvider.GetService<CloudPairingService>();
      if (pairing == null) return null;
      using var identity = await pairing.GetIdentityAsync(ct);
      if (identity == null) return null;
      // The pairing credential is the server's login at the Cloud. It goes only to the Cloud's own host, never to a relay
      // somewhere else that PushRelay:Url happens to name (use PushRelay:ServerKey for a relay that is not the Cloud).
      if (_pushUri == null || !IsSameCloudHost(identity.CloudUrl, _pushUri))
      {
        _logger.LogWarning("PushRelay:Url is not the paired Cloud host, so the Cloud credential is not sent to it");
        return null;
      }
      return identity.Credential;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      _logger.LogWarning("Could not read the Cloud pairing for the push relay: {Kind}", ex.GetType().Name);
      return null;
    }
  }
}
