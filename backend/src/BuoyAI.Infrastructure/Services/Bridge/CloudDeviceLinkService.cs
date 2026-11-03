using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuoyAI.Infrastructure.Services.Bridge;

/// <summary>What the admin's screen shows after starting a code link.</summary>
public sealed record DeviceLinkStart(string UserCode, string VerificationUri, string VerificationUriComplete, DateTime ExpiresAt, int Interval);

/// <summary>
/// Where the code link stands: <c>none</c>, <c>waiting</c>, <c>linked</c>, <c>expired</c>, <c>denied</c> or <c>failed</c>.
/// The code, its expiry and the approve page come along while there is a session, so a reopened screen can show them again.
/// </summary>
public sealed record DeviceLinkStatus(string State, string? UserCode = null, DateTime? ExpiresAt = null,
  string? VerificationUri = null, string? VerificationUriComplete = null, string? Message = null);

/// <summary>
/// "Link with BuoyAI Cloud" by code (RFC 8628 device authorization). The server asks the Cloud for a short code and
/// shows it; the owner approves that code on the Cloud page from any device; this service polls in the background and,
/// once approved, pairs with the one-time code the Cloud hands back. Works for plain http servers, because nothing
/// ever comes back to this server's address. One session at a time, held in memory (a self-hosted server is one
/// process): the device code never leaves this class, and a restart simply drops a pending link.
/// </summary>
public sealed class CloudDeviceLinkService : IDisposable
{
  public const int MaxNameLength = 60;
  private static readonly TimeSpan SlowDownStep = TimeSpan.FromSeconds(5);

  private readonly IHttpClientFactory _http;
  private readonly IServiceScopeFactory _scopes;
  private readonly CloudBridgeOptions _options;
  private readonly TimeProvider _time;
  private readonly ILogger<CloudDeviceLinkService> _logger;
  private readonly object _gate = new();
  // One pairing change at a time: the code link's own pairing, a pasted code, the browser callback and unlink all take
  // this before they read or write the stored credential, so none of them can overwrite or undo another mid-flight.
  private readonly SemaphoreSlim _pairingGuard = new(1, 1);
  private Session? _session;

  public CloudDeviceLinkService(IHttpClientFactory http, IServiceScopeFactory scopes, IOptions<CloudBridgeOptions> options,
    TimeProvider time, ILogger<CloudDeviceLinkService> logger)
  {
    _http = http;
    _scopes = scopes;
    _options = options.Value;
    _time = time;
    _logger = logger;
    Delay = (wait, ct) => Task.Delay(wait, _time, ct);
  }

  /// <summary>How the poll loop waits between polls. Tests swap it for one that returns at once.</summary>
  internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; }

  /// <summary>The running poll loop, or a finished task when there is none. For tests.</summary>
  internal Task PollLoop
  {
    get
    {
      lock (_gate) return _session?.Loop ?? Task.CompletedTask;
    }
  }

  private sealed class Session
  {
    public required int UserId { get; init; }
    public required string Name { get; init; }
    public required string DeviceCode { get; init; }
    public required string UserCode { get; init; }
    public required string VerificationUri { get; init; }
    public required string VerificationUriComplete { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required TimeSpan Interval { get; init; }
    public CancellationTokenSource Cancel { get; } = new();
    public Task Loop { get; set; } = Task.CompletedTask;
    public string State { get; set; } = "waiting";
    public string? Message { get; set; }
    /// <summary>A poll or the pairing after an approval is running. Only the loop decides how that ends.</summary>
    public bool Busy { get; set; }
  }

  /// <summary>
  /// Asks the Cloud for a code and starts polling. Replaces any session already running. Throws
  /// <see cref="DomainValidationException"/> with a message safe to show when the Cloud cannot be asked.
  /// </summary>
  public async Task<DeviceLinkStart> StartAsync(int userId, string? name, CancellationToken ct = default)
  {
    var cloud = CloudPairingService.ParseCloudUrl(_options.Url)
                ?? throw new DomainValidationException("The Cloud bank feed is not set up on this server (CloudBridge:Url).");
    var displayName = string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim();
    if (displayName.Length > MaxNameLength) displayName = displayName[..MaxNameLength];

    JsonNode? answer;
    try
    {
      var client = _http.CreateClient(CloudPairingService.HttpClientName);
      using var response = await client.PostAsJsonAsync(new Uri(cloud, "v1/pairing/link-sessions"), new
      {
        name = displayName,
        serverVersion = CloudPairingService.ServerVersion(_options)
      }, ct);
      var text = await response.Content.ReadAsStringAsync(ct);
      if (response.StatusCode == HttpStatusCode.TooManyRequests)
        throw new DomainValidationException("Too many link attempts from this server. Try again in a while.");
      if (!response.IsSuccessStatusCode)
      {
        _logger.LogWarning("The Cloud refused a link session: {Status}", (int)response.StatusCode);
        throw new DomainValidationException("The BuoyAI Cloud could not start a link. Try again.");
      }
      answer = JsonNode.Parse(text);
    }
    catch (HttpRequestException ex)
    {
      _logger.LogWarning("Cloud link session request failed: {Kind}", ex.GetType().Name);
      throw new DomainValidationException("Could not reach the BuoyAI Cloud. Check this server's internet connection.");
    }
    catch (Exception ex) when (ex is System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
    {
      throw new DomainValidationException("Could not reach the BuoyAI Cloud. Check this server's internet connection.");
    }

    var deviceCode = Text(answer, "deviceCode");
    var userCode = Text(answer, "userCode");
    var verificationUri = WebAddress(Text(answer, "verificationUri"));
    var verificationUriComplete = WebAddress(Text(answer, "verificationUriComplete")) ?? verificationUri;
    var expiresIn = Number(answer, "expiresIn") ?? 600;
    var interval = Math.Clamp(Number(answer, "interval") ?? 5, 1, 60);
    if (deviceCode == null || userCode == null || verificationUri == null || verificationUriComplete == null || expiresIn <= 0)
      throw new DomainValidationException("The Cloud answered in a way this server does not understand.");

    var session = new Session
    {
      UserId = userId,
      Name = displayName,
      DeviceCode = deviceCode,
      UserCode = userCode,
      VerificationUri = verificationUri,
      VerificationUriComplete = verificationUriComplete,
      ExpiresAt = _time.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(expiresIn, 3600)),
      Interval = TimeSpan.FromSeconds(interval)
    };

    lock (_gate)
    {
      Stop(_session);
      _session = session;
      var token = session.Cancel.Token;
      session.Loop = Task.Run(() => PollLoopAsync(session, token), CancellationToken.None);
    }

    _logger.LogInformation("Cloud code link started by admin {UserId}", userId);
    return new DeviceLinkStart(userCode, verificationUri, verificationUriComplete, session.ExpiresAt.UtcDateTime, interval);
  }

  public DeviceLinkStatus GetStatus()
  {
    lock (_gate)
    {
      var s = _session;
      if (s == null) return new DeviceLinkStatus("none");
      // The loop may be mid-wait when the clock passes the expiry; the answer must not lag behind it. While a poll or an
      // approved pairing is running, though, the loop decides: an approval just before the deadline can still link.
      var state = s.State == "waiting" && !s.Busy && _time.GetUtcNow() >= s.ExpiresAt ? "expired" : s.State;
      return new DeviceLinkStatus(state, s.UserCode, s.ExpiresAt.UtcDateTime, s.VerificationUri, s.VerificationUriComplete,
        state == "failed" ? s.Message : null);
    }
  }

  /// <summary>Stops polling and forgets the session. Nothing on the Cloud changes; the code just runs out.</summary>
  public void Cancel()
  {
    lock (_gate)
    {
      Stop(_session);
      _session = null;
    }
  }

  public void Dispose() => Cancel();

  /// <summary>
  /// Runs another way of changing the pairing (a pasted code, the browser callback, unlink) under the same guard as the
  /// code link's own pairing, then stops any code link still waiting so it cannot pair over the result later. When
  /// <paramref name="work"/> throws, the waiting code link is left alone.
  /// </summary>
  public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
  {
    await _pairingGuard.WaitAsync(ct);
    try
    {
      var result = await work();
      Cancel();
      return result;
    }
    finally
    {
      _pairingGuard.Release();
    }
  }

  private static void Stop(Session? session)
  {
    if (session == null) return;
    try { session.Cancel.Cancel(); }
    catch (ObjectDisposedException) { }
  }

  private async Task PollLoopAsync(Session session, CancellationToken token)
  {
    var interval = session.Interval;
    try
    {
      while (true)
      {
        await Delay(interval, token);
        token.ThrowIfCancellationRequested();
        if (_time.GetUtcNow() >= session.ExpiresAt)
        {
          Finish(session, "expired");
          return;
        }

        SetBusy(session, true);
        var (outcome, code) = await PollOnceAsync(session, token);
        switch (outcome)
        {
          case "pending":
            SetBusy(session, false);
            continue;
          case "slow_down":
            interval += SlowDownStep;
            SetBusy(session, false);
            continue;
          case "expired":
          case "denied":
            Finish(session, outcome);
            return;
          case "approved" when code != null:
            await PairAsync(session, code, token);
            return;
          default:
            Finish(session, "failed", "Linking did not work. Try again.");
            return;
        }
      }
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested)
    {
      // Cancelled or replaced: nothing to report.
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "The Cloud code link loop stopped");
      Finish(session, "failed", "Linking did not work. Try again.");
    }
  }

  /// <summary>One poll. Network trouble and Cloud hiccups read as "pending" so the loop simply tries again.</summary>
  private async Task<(string Outcome, string? Code)> PollOnceAsync(Session session, CancellationToken token)
  {
    var cloud = CloudPairingService.ParseCloudUrl(_options.Url);
    if (cloud == null) return ("failed", null);
    try
    {
      var client = _http.CreateClient(CloudPairingService.HttpClientName);
      using var response = await client.PostAsJsonAsync(new Uri(cloud, "v1/pairing/link-sessions/poll"),
        new { deviceCode = session.DeviceCode }, token);
      var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(token));
      switch (response.StatusCode)
      {
        case HttpStatusCode.OK:
          var status = Text(json, "status");
          return status == "approved" ? ("approved", Text(json, "code")) : (status ?? "pending", null);
        case HttpStatusCode.TooManyRequests:
          return ("slow_down", null);
        case HttpStatusCode.Gone:
          return (Text(json, "error") == "denied" ? "denied" : "expired", null);
        default:
          if ((int)response.StatusCode >= 500) return ("pending", null);
          _logger.LogWarning("The Cloud refused a link poll: {Status}", (int)response.StatusCode);
          return ("failed", null);
      }
    }
    catch (HttpRequestException ex)
    {
      _logger.LogWarning("Cloud link poll failed: {Kind}", ex.GetType().Name);
      return ("pending", null);
    }
    catch (System.Text.Json.JsonException)
    {
      return ("pending", null);
    }
    catch (TaskCanceledException) when (!token.IsCancellationRequested)
    {
      return ("pending", null); // the request timed out
    }
  }

  private async Task PairAsync(Session session, string code, CancellationToken token)
  {
    // Wait for any other pairing change to finish, then check again: a cancel (from a pasted code, the browser callback
    // or unlink, which all cancel under the guard) that came in meanwhile wins.
    await _pairingGuard.WaitAsync(token);
    try
    {
      token.ThrowIfCancellationRequested();
      await using var scope = _scopes.CreateAsyncScope();
      var pairing = scope.ServiceProvider.GetRequiredService<ICloudPairingService>();
      // Linked another way (pasted code, browser round trip) while this code waited: keep that credential.
      if ((await pairing.GetStatusAsync(CancellationToken.None)).Paired)
      {
        Finish(session, "linked");
        _logger.LogInformation("Cloud code link for admin {UserId} skipped: the server is already linked", session.UserId);
        return;
      }
      // Not cancellable: once the code is spent, the credential has to be stored.
      await pairing.PairAsync(code, session.Name, CancellationToken.None);
      Finish(session, "linked");
      _logger.LogInformation("Cloud code link completed for admin {UserId}", session.UserId);
    }
    catch (DomainValidationException ex)
    {
      _logger.LogWarning("Cloud code link for admin {UserId} did not pair: {Reason}", session.UserId, ex.Message);
      Finish(session, "failed", ex.Message);
    }
    finally
    {
      _pairingGuard.Release();
    }
  }

  private void SetBusy(Session session, bool busy)
  {
    lock (_gate) session.Busy = busy;
  }

  private void Finish(Session session, string state, string? message = null)
  {
    lock (_gate)
    {
      session.State = state;
      session.Message = message;
    }
  }

  private static string? Text(JsonNode? node, string key)
  {
    try
    {
      var value = node?[key]?.GetValue<string>();
      return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
    catch (InvalidOperationException) { return null; }
  }

  private static int? Number(JsonNode? node, string key)
  {
    try { return node?[key]?.GetValue<int>(); }
    catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return null; }
  }

  /// <summary>The approve page must be https (plain http only on this machine, for development).</summary>
  private static string? WebAddress(string? url)
  {
    if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
    var ok = uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
    return ok && string.IsNullOrEmpty(uri.UserInfo) ? url : null;
  }
}
