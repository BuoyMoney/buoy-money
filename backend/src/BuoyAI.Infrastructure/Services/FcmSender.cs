using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// FCM HTTP v1 sender via the FirebaseAdmin SDK. Initialized once (singleton) from the
/// service-account credential, resolved in order: Firebase:CredentialsJson (the raw JSON —
/// how deploys inject it, from the env-scoped FIREBASE_ADMIN_CREDENTIALS GitHub secret),
/// then Firebase:CredentialsPath (a file path — local dev), then the standard
/// GOOGLE_APPLICATION_CREDENTIALS env var. When none is present the sender stays disabled
/// and every send is a no-op — environments without a Firebase project keep working.
/// </summary>
public class FcmSender : IFcmSender
{
  private readonly ILogger<FcmSender> _logger;
  private readonly FirebaseMessaging? _messaging;

  public bool IsEnabled => _messaging != null;

  public FcmSender(IConfiguration configuration, ILogger<FcmSender> logger)
  {
    _logger = logger;

    var credentialsJson = configuration["Firebase:CredentialsJson"];
    var credentialsPath = configuration["Firebase:CredentialsPath"];
    var envCredentials = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");

    try
    {
      GoogleCredential? credential = null;
      if (!string.IsNullOrWhiteSpace(credentialsJson))
      {
        credential = CredentialFactory.FromJson<ServiceAccountCredential>(credentialsJson).ToGoogleCredential();
      }
      else if (!string.IsNullOrWhiteSpace(credentialsPath) && File.Exists(credentialsPath))
      {
        credential = CredentialFactory.FromFile<ServiceAccountCredential>(credentialsPath).ToGoogleCredential();
      }
      else if (!string.IsNullOrWhiteSpace(envCredentials) && File.Exists(envCredentials))
      {
        credential = GoogleCredential.GetApplicationDefault();
      }

      if (credential == null)
      {
        _logger.LogInformation(
          "Firebase credentials not configured (Firebase:CredentialsJson / Firebase:CredentialsPath / GOOGLE_APPLICATION_CREDENTIALS) — push notifications disabled");
        return;
      }

      // FirebaseApp.Create throws if the default app already exists (e.g. WebApplicationFactory
      // spinning up multiple hosts in integration tests) — reuse it instead.
      var app = FirebaseApp.DefaultInstance ?? FirebaseApp.Create(new AppOptions { Credential = credential });
      _messaging = FirebaseMessaging.GetMessaging(app);
      _logger.LogInformation("Firebase Cloud Messaging initialized — push notifications enabled");
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to initialize Firebase Cloud Messaging — push notifications disabled");
    }
  }

  public async Task<FcmSendResult> SendAsync(IReadOnlyList<string> tokens, string title, string body,
    IReadOnlyDictionary<string, string> data, CancellationToken cancellationToken = default)
  {
    if (_messaging == null || tokens.Count == 0)
    {
      return FcmSendResult.Empty;
    }

    // MulticastMessage.Tokens is marked obsolete in favor of Fids, but Fids are Firebase
    // *installation* IDs — a different identifier from the FCM registration tokens the
    // clients hand us. Token targeting remains fully supported; keep using it.
    var message = BuildMessage(tokens, title, body, data);

    var response = await _messaging.SendEachForMulticastAsync(message, cancellationToken);

    var invalidTokens = new List<string>();
    for (var i = 0; i < response.Responses.Count; i++)
    {
      var send = response.Responses[i];
      if (send.IsSuccess)
      {
        continue;
      }

      // Unregistered = the install is gone (uninstall/expired); InvalidArgument = the token
      // itself is malformed. Both are permanent — hand them back for pruning. Everything
      // else (Unavailable, Internal, QuotaExceeded) is transient and just logged.
      if (send.Exception is FirebaseMessagingException fme &&
          (fme.MessagingErrorCode == MessagingErrorCode.Unregistered ||
           fme.MessagingErrorCode == MessagingErrorCode.InvalidArgument))
      {
        invalidTokens.Add(tokens[i]);
      }
      else
      {
        _logger.LogWarning(send.Exception, "Transient FCM send failure for one device");
      }
    }

    return new FcmSendResult(response.SuccessCount, response.FailureCount, invalidTokens);
  }

  /// <summary>
  /// Builds the multicast payload. Extracted from SendAsync so the payload can be
  /// asserted in tests without a live Firebase app, which is how the year-1
  /// EventTimestamp shipped unnoticed in the first place.
  /// </summary>
#pragma warning disable CS0618
  internal static MulticastMessage BuildMessage(
    IReadOnlyList<string> tokens, string title, string body,
    IReadOnlyDictionary<string, string> data) =>
    new()
    {
      // MulticastMessage.Tokens is marked obsolete in favor of Fids, but Fids are Firebase
      // *installation* IDs — a different identifier from the FCM registration tokens the
      // clients hand us. Token targeting remains fully supported; keep using it.
      Tokens = tokens.ToList(),
      Notification = new Notification { Title = title, Body = body },
      Data = new Dictionary<string, string>(data),
      Android = new AndroidConfig
      {
        Priority = Priority.High,
        Notification = new AndroidNotification
        {
          ChannelId = "buoyai_alerts",
          // MUST be set explicitly. AndroidNotification.EventTimestamp is a
          // non-nullable DateTime, so leaving it alone does not mean "omit it" — it
          // means DateTime.MinValue, and the SDK serialises that into the payload as
          // year 1. Android sorts and labels notifications by this field, so every
          // push we had ever sent displayed its age as roughly two thousand years
          // ("buoy_ai - 2032y" in the shade) and sorted to the bottom of the panel.
          EventTimestamp = DateTime.UtcNow
        }
      },
      Apns = new ApnsConfig
      {
        Aps = new Aps { Sound = "default" }
      }
    };
#pragma warning restore CS0618
}
