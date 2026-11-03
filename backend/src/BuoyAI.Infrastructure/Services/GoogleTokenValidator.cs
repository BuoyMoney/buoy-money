using Google.Apis.Auth;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Default <see cref="IGoogleTokenValidator"/> backed by the Google Auth SDK's
/// <see cref="GoogleJsonWebSignature"/> validation, with the token audience
/// pinned to our own client IDs so a token minted for any other Google project
/// is rejected.
/// </summary>
public class GoogleTokenValidator : IGoogleTokenValidator
{
  private readonly IReadOnlyList<string> _allowedAudiences;
  private readonly ILogger<GoogleTokenValidator> _logger;

  public GoogleTokenValidator(IConfiguration configuration, ILogger<GoogleTokenValidator> logger)
  {
    _logger = logger;
    _allowedAudiences = ResolveAllowedAudiences(configuration);
  }

  /// <summary>
  /// The app's public web OAuth client ID. It is used as the client ID on web and
  /// as the mobile <c>serverClientId</c>, so it is the <c>aud</c> of every Google ID
  /// token we receive. Mirrors <c>flutter/web/index.html</c> and
  /// <c>app_config.dart</c> — a public identifier, not a secret. Baked in as the
  /// default so a deploy that ships no <c>Google</c> config still pins the audience.
  /// </summary>
  internal const string DefaultWebClientId =
    "841207882659-kink79fsnp2rvtt44cmur4ss7cuuoibm.apps.googleusercontent.com";

  /// <summary>
  /// The native iOS OAuth client (GIDClientID in flutter/ios/Runner/Info.plist). On iOS,
  /// google_sign_in 7.x mints the ID token against THIS client, not the Dart-side
  /// serverClientId, so every iOS login carries it as <c>aud</c>. Same GCP project, public
  /// identifier, baked in for the same reason as <see cref="DefaultWebClientId"/>. Found the
  /// hard way: the 1.5.22 TestFlight build failed every Google login with "untrusted 'aud'".
  /// </summary>
  internal const string DefaultIosClientId =
    "841207882659-hioovd4rgi0s6cr869ajqnrl1rnsnnfu.apps.googleusercontent.com";

  /// <summary>
  /// The audiences a valid Google ID token may carry — the <b>client-side</b> Google
  /// client IDs that actually mint the tokens (web + mobile <c>serverClientId</c>,
  /// plus any future native iOS/Android client IDs).
  ///
  /// The set is the union of, in order:
  /// <list type="number">
  ///   <item><c>Google:AllowedAudiences</c> — explicit override (e.g. extra native client IDs).</item>
  ///   <item><c>GoogleAuth:ClientId</c> — the deployed OAuth client. In this project the
  ///     Flutter web/mobile builds are compiled with the <em>same</em> client ID that the
  ///     backend carries here (CI bakes in <c>secrets.GOOGLE_CLIENT_ID</c> via
  ///     <c>--dart-define</c>/build-arg), so it is the <c>aud</c> of every real token. It
  ///     must be trusted or every deployed Google login fails with an untrusted-aud error.</item>
  ///   <item><see cref="DefaultWebClientId"/> — a baked-in fallback so a deploy that ships
  ///     no Google config (e.g. local dev) still pins the audience to a known value.</item>
  /// </list>
  /// De-duplicated; blanks dropped.
  /// </summary>
  internal static IReadOnlyList<string> ResolveAllowedAudiences(IConfiguration configuration)
  {
    var audiences = new List<string>();

    var overrides = configuration.GetSection("Google:AllowedAudiences").Get<string[]>();
    if (overrides is { Length: > 0 })
      audiences.AddRange(overrides);

    // The deployed apps are built with the same client ID the backend holds as
    // GoogleAuth:ClientId, so that ID is the audience of every token we receive.
    audiences.Add(configuration["GoogleAuth:ClientId"] ?? "");

    // A self-hosted server trusts only the operator's own client. BuoyAI's hosted client IDs are
    // left out, so a token minted for the hosted service cannot be replayed on a home server.
    var selfHost = BuoyAI.Infrastructure.Configuration.EditionInfo.From(configuration).IsSelfHost;
    if (!selfHost)
    {
      audiences.Add(DefaultWebClientId);
      audiences.Add(DefaultIosClientId);
    }

    var resolved = audiences
      .Where(a => !string.IsNullOrWhiteSpace(a))
      .Select(a => a.Trim())
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();

    // Hosted is never empty (DefaultWebClientId is always present). Self-host with nothing
    // configured accepts no audience at all, which rejects every token.
    if (resolved.Length > 0) return resolved;
    return selfHost ? Array.Empty<string>() : new[] { DefaultWebClientId };
  }

  public async Task<GoogleTokenPayload?> ValidateAsync(string idToken)
  {
    if (string.IsNullOrWhiteSpace(idToken))
      return null;

    GoogleJsonWebSignature.Payload? payload;
    try
    {
      var settings = new GoogleJsonWebSignature.ValidationSettings
      {
        Audience = _allowedAudiences
      };
      payload = await GoogleJsonWebSignature.ValidateAsync(idToken, settings);
    }
    catch (InvalidJwtException ex)
    {
      // Thrown on a bad signature, wrong issuer, or an audience outside the
      // pinned set — all of which must be treated as an invalid login.
      _logger.LogWarning(ex, "Google ID token rejected during validation.");
      return null;
    }

    if (payload == null)
      return null;

    return new GoogleTokenPayload
    {
      Subject = payload.Subject,
      Email = payload.Email,
      // Surface Google's email_verified so the auth service can refuse to resolve/link an
      // account by an unverified email (account-takeover guard). Defaults to false.
      EmailVerified = payload.EmailVerified,
      GivenName = payload.GivenName,
      FamilyName = payload.FamilyName,
      Picture = payload.Picture
    };
  }
}
