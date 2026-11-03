namespace BuoyAI.Core.DTOs.Auth;

public class LoginRequest
{
  public string Email { get; set; } = string.Empty;
  public string Password { get; set; } = string.Empty;
}

public class UpdateProfileRequest
{
  public string? FirstName { get; set; }
  public string? LastName { get; set; }

  /// <summary>
  /// ISO-4217 primary display currency. Optional; when sent it must be one of
  /// <see cref="BuoyAI.Core.ValueObjects.CurrencyCodes.Supported"/>. Setting it marks the choice
  /// as explicit, so a later bank link never overrides it. (#941 step 2)
  /// </summary>
  public string? Currency { get; set; }

  /// <summary>
  /// First day of the budgeting week (0=Sunday..6=Saturday). Optional; setting it
  /// re-bases the user's active Weekly/BiWeekly budget periods to the new anchor. (#941)
  /// </summary>
  public int? WeekStartDay { get; set; }
}

public class LoginResponse
{
  public string Token { get; set; } = string.Empty;
  public UserProfileDto User { get; set; } = new();
  public DateTime ExpiresAt { get; set; }

  /// <summary>Rotating refresh token. Sent in the body for native clients; on web it
  /// is delivered as an HttpOnly cookie instead and this may be null.</summary>
  public string? RefreshToken { get; set; }

  public List<PendingFamilyInvitationDto> PendingFamilyInvitations { get; set; } = new();

  /// <summary>
  /// True only when this request created the account. OAuth sign-up and sign-in are the
  /// same call, so the client cannot tell them apart on its own — without this, a Google
  /// or Apple sign-up is indistinguishable from a returning login and never counts as a
  /// signup in the activation funnel.
  /// </summary>
  public bool IsNewUser { get; set; }
}

/// <summary>Body for <c>POST /auth/refresh</c> and <c>/auth/logout</c> from native
/// clients that carry the refresh token themselves (web uses the HttpOnly cookie).</summary>
public class RefreshTokenRequest
{
  public string? RefreshToken { get; set; }
}

/// <summary>Body for <c>POST /auth/verify-password</c>: a lightweight check of the current
/// user's password (e.g. to gate enabling quick-login) that mints no tokens and creates no
/// session — see <see cref="VerifyPasswordResponse"/>.</summary>
public class VerifyPasswordRequest
{
  public string Password { get; set; } = string.Empty;
}

/// <summary>Result of <c>POST /auth/verify-password</c>. <c>Valid</c> is false for a wrong
/// password AND for accounts with no password (Google/Apple/MFA-only) — never throws.</summary>
public class VerifyPasswordResponse
{
  public bool Valid { get; set; }
}

public class PendingFamilyInvitationDto
{
  public string InvitationToken { get; set; } = string.Empty;
  public string FamilyName { get; set; } = string.Empty;
  public string InvitedByName { get; set; } = string.Empty;
  public string InvitedRole { get; set; } = string.Empty;
  public DateTime ExpiresAt { get; set; }
}

public class UserProfileDto
{
  public int Id { get; set; }
  public string Username { get; set; } = string.Empty;
  public string Email { get; set; } = string.Empty;
  public string FirstName { get; set; } = string.Empty;
  public string LastName { get; set; } = string.Empty;
  public string Currency { get; set; } = string.Empty;

  /// <summary>See <see cref="UpdateProfileRequest.WeekStartDay"/>. Null = legacy (Sunday weeks).</summary>
  public int? WeekStartDay { get; set; }
  public string? ProfilePictureUrl { get; set; }
  /// <summary>
  /// Non-null when the user has uploaded a custom avatar (served at
  /// GET /api/user/avatar/{id}). Doubles as a cache-busting version. Null = none.
  /// </summary>
  public DateTime? AvatarUpdatedAt { get; set; }
  public bool IsGoogleUser { get; set; }
  public bool IsAppleUser { get; set; }
  /// <summary>
  /// True when the account has a local BuoyAI password (a non-empty PasswordHash).
  /// False for OAuth-only accounts (Google/Apple sign-in with no password set).
  /// The client uses this to only offer password-based features — e.g. biometric
  /// "quick login" replays a stored password, which OAuth-only accounts don't have.
  /// </summary>
  public bool HasPassword { get; set; }
  public string SubscriptionTier { get; set; } = "Free";
  public bool MfaEnabled { get; set; }
  public DateTime? TrialEndsAt { get; set; }
  public DateTime CreatedAt { get; set; }
  public int? FamilyId { get; set; }
  public string? FamilyRole { get; set; }

  // True when the account must choose and pay for a plan before using the app
  // (returning user whose email already used its free trial). Drives the paywall.
  public bool RequiresPaidPlan { get; set; }

  // True once the user's email address has been verified. Required for protected
  // operations (e.g. connecting a bank account).
  public bool IsEmailVerified { get; set; }

  // When the user granted permission to share their data with third-party AI (Gemini).
  // Null = not granted; the client shows the consent sheet before the first AI feature.
  public DateTime? AiConsentGrantedAt { get; set; }
  // Explicit opt-out timestamp (null unless the user turned AI features off).
  public DateTime? AiConsentWithdrawnAt { get; set; }
}

/// <summary>Body for PUT /user/ai-consent.</summary>
public class UpdateAiConsentRequest
{
  public bool Granted { get; set; }
}

/// <summary>Request to confirm email verification with the emailed code.</summary>
public class ConfirmEmailVerificationRequest
{
  public string Code { get; set; } = string.Empty;
}

public class GoogleLoginRequest
{
  public string IdToken { get; set; } = string.Empty;

  /// <summary>
  /// True when the request originates from the registration/sign-up flow.
  /// When false (login flow) and no account exists for the Google email, the
  /// backend returns a "no account" error so the client can redirect to register.
  /// </summary>
  public bool IsRegistration { get; set; }

  /// <summary>Self-host only: one-time code from an admin, needed while registration is closed.</summary>
  public string? InviteCode { get; set; }

  /// <summary>
  /// "cloud" when the sign-up started on the Cloud portal (buoy-ai.com/cloud/): the new account is Cloud-only
  /// (no hosted trial, no hosted onboarding). Ignored for an existing account and on self-host.
  /// </summary>
  public string? SignupContext { get; set; }
}

public class AppleLoginRequest
{
  public string IdentityToken { get; set; } = string.Empty;
  public string AuthorizationCode { get; set; } = string.Empty;
  public string Nonce { get; set; } = string.Empty;
  public string? Email { get; set; }
  public string? FirstName { get; set; }
  public string? LastName { get; set; }
  public string UserIdentifier { get; set; } = string.Empty;

  /// <summary>
  /// "cloud" when the sign-up started on the Cloud portal (buoy-ai.com/cloud/): the new account is Cloud-only
  /// (no hosted trial, no hosted onboarding). Ignored for an existing account and on self-host.
  /// </summary>
  public string? SignupContext { get; set; }
}

public class RegisterRequest
{
  public string Email { get; set; } = string.Empty;
  public string Password { get; set; } = string.Empty;
  public string FirstName { get; set; } = string.Empty;
  public string LastName { get; set; } = string.Empty;

  /// <summary>Self-host only: one-time code from an admin, needed while registration is closed.</summary>
  public string? InviteCode { get; set; }

  /// <summary>
  /// "cloud" when the sign-up started on the Cloud portal (buoy-ai.com/cloud/): the new account is Cloud-only
  /// (no hosted trial, no hosted onboarding). Ignored for an existing account and on self-host.
  /// </summary>
  public string? SignupContext { get; set; }
}

/// <summary>
/// Provider-agnostic view of a verified Google ID token. Decouples the auth
/// service from the Google SDK's payload type so token validation can be mocked.
/// </summary>
public class GoogleTokenPayload
{
  public string Subject { get; set; } = string.Empty;
  public string Email { get; set; } = string.Empty;
  /// <summary>
  /// Google's <c>email_verified</c> claim. False for e.g. a non-Gmail address a user
  /// added to their Google account but never verified. Must be true before we trust the
  /// email to resolve or link an account, or an unverified email allows account takeover.
  /// </summary>
  public bool EmailVerified { get; set; }
  public string? GivenName { get; set; }
  public string? FamilyName { get; set; }
  public string? Picture { get; set; }
}

/// <summary>
/// Provider-agnostic view of a verified Apple ID token. Only populated after the
/// token's signature, issuer, audience, and expiry have been validated against
/// Apple's JWKS — never trust these fields from an unverified token.
/// </summary>
public class AppleTokenPayload
{
  public string Subject { get; set; } = string.Empty;
  public string? Email { get; set; }
  public bool EmailVerified { get; set; }
}

/// <summary>
/// A verified Apple "Sign in with Apple" server-to-server notification event. Only
/// populated after the signed payload passes JWKS signature + issuer + audience
/// verification. See <c>IAppleTokenValidator.ValidateServerNotificationAsync</c>.
/// </summary>
public class AppleServerNotification
{
  /// <summary>e.g. "account-delete", "consent-revoked", "email-disabled", "email-enabled".</summary>
  public string Type { get; set; } = string.Empty;
  /// <summary>The user's stable Apple identifier (matches <c>User.AppleId</c>).</summary>
  public string Subject { get; set; } = string.Empty;
  public string? Email { get; set; }
}

public class GoogleUserInfo
{
  public string Id { get; set; } = string.Empty;
  public string Email { get; set; } = string.Empty;
  public string FirstName { get; set; } = string.Empty;
  public string LastName { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public string Picture { get; set; } = string.Empty;
}

public class UserMfaDetailsDto
{
  public string MfaType { get; set; } = string.Empty;
  public bool HasMfaEnabled { get; set; }
  public bool HasPasskeyRegistered { get; set; }
  public DateTime? MfaEnabledAt { get; set; }
  public DateTime? MfaGracePeriodEndsAt { get; set; }
}
