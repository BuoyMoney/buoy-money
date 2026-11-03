using Microsoft.Extensions.Configuration;

namespace BuoyAI.Infrastructure.Configuration;

/// <summary>
/// Single sign-on with an OpenID Connect provider (Pocket ID, Authentik, Keycloak) on a self-hosted
/// server. Read from the <c>Oidc</c> section on every use, so a restart with new settings is all it
/// takes. See docs/self_host/sign_in_email_push.md.
/// </summary>
public sealed class OidcOptions
{
  public const string DefaultDisplayName = "Single sign-on";

  /// <summary>Oidc:Enabled. Off unless the operator turns it on.</summary>
  public bool Enabled { get; init; }

  /// <summary>Oidc:Authority. The provider's issuer URL; discovery is read from
  /// <c>{Authority}/.well-known/openid-configuration</c>.</summary>
  public string? Authority { get; init; }

  public string? ClientId { get; init; }

  /// <summary>Oidc:ClientSecret. Optional: without it the server signs in as a public client with PKCE only.</summary>
  public string? ClientSecret { get; init; }

  /// <summary>Oidc:DisplayName. The button reads "Sign in with {DisplayName}".</summary>
  public string DisplayName { get; init; } = DefaultDisplayName;

  /// <summary>Oidc:AutoRegister. When on, anyone the registration rules let in gets an account on
  /// first sign-in. When off (default), only existing accounts, the admin email and invite holders.</summary>
  public bool AutoRegister { get; init; }

  /// <summary>Oidc:DisablePasswordLogin. Hides the password form and refuses password sign-in. Only
  /// applies while single sign-on is configured; set it false again to get password sign-in back.</summary>
  public bool DisablePasswordLogin { get; init; }

  /// <summary>Oidc:TrustUnverifiedEmail. Accept an email the provider does not mark verified.</summary>
  public bool TrustUnverifiedEmail { get; init; }

  /// <summary>Oidc:SkipAppMfa. Treat a provider sign-in as the second factor (for passkey-only
  /// providers like Pocket ID) instead of asking for the app's own MFA too.</summary>
  public bool SkipAppMfa { get; init; }

  /// <summary>Oidc:RedirectUri. Overrides <c>{App:BaseUrl}/api/auth/oidc/callback</c>.</summary>
  public string? RedirectUri { get; init; }

  /// <summary>App:BaseUrl, the server's public address (BUOY_ORIGIN).</summary>
  public string? PublicOrigin { get; init; }

  /// <summary>True when sign-in can actually run: turned on, with a provider and a client id. The
  /// provider address must be https (plain http only for a loopback provider on the same machine),
  /// because the signing keys and token exchange travel over it.</summary>
  public bool IsConfigured =>
    Enabled &&
    Uri.TryCreate(Authority, UriKind.Absolute, out var a) && (a.Scheme == Uri.UriSchemeHttps || (a.Scheme == Uri.UriSchemeHttp && a.IsLoopback)) &&
    !string.IsNullOrWhiteSpace(ClientId);

  /// <summary>Password sign-in is off only when single sign-on works, so a broken provider setup
  /// can never lock everyone out.</summary>
  public bool PasswordLoginDisabled => IsConfigured && DisablePasswordLogin;

  public static OidcOptions From(IConfiguration configuration)
  {
    var section = configuration.GetSection("Oidc");
    var name = section["DisplayName"];
    return new OidcOptions
    {
      Enabled = section.GetValue<bool>("Enabled"),
      Authority = Trim(section["Authority"]),
      ClientId = Trim(section["ClientId"]),
      ClientSecret = Trim(section["ClientSecret"]),
      DisplayName = string.IsNullOrWhiteSpace(name) ? DefaultDisplayName : name.Trim(),
      AutoRegister = section.GetValue<bool>("AutoRegister"),
      DisablePasswordLogin = section.GetValue<bool>("DisablePasswordLogin"),
      TrustUnverifiedEmail = section.GetValue<bool>("TrustUnverifiedEmail"),
      SkipAppMfa = section.GetValue<bool>("SkipAppMfa"),
      RedirectUri = Trim(section["RedirectUri"]),
      PublicOrigin = Trim(configuration["App:BaseUrl"])?.TrimEnd('/'),
    };
  }

  private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
