using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Services;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Services.Oidc;

namespace BuoyAI.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("auth")]
public class AuthController : BaseController
{
  private readonly IAuthService _authService;
  private readonly IDeviceTrustService _deviceTrustService;
  private readonly ITwoFactorService _twoFactorService;
  private readonly IStepUpAuthService _stepUpAuthService;
  private readonly IIpGeolocationService _geolocationService;
  private readonly IMfaPreferencesService _mfaPreferencesService;
  private readonly IEmailService _emailService;
  private readonly IRefreshTokenService _refreshTokenService;
  private readonly IUserRepository _userRepository;
  private readonly IConfiguration _configuration;
  private readonly ILogger<AuthController> _logger;

  /// <summary>Cookie carrying the refresh token on web (HttpOnly, so JS/XSS can't read it).</summary>
  private const string HostedRefreshCookieName = "refresh_token";

  /// <summary>A self-hosted server uses its own cookie name. Browsers share cookies across ports
  /// on one host name, so two BuoyAI servers on localhost (or a LAN name) with the same cookie
  /// name overwrite and clear each other's session on every reload.</summary>
  private const string SelfHostRefreshCookieName = "buoy_money_refresh";

  private string RefreshCookieName =>
    HttpContext.RequestServices?.GetService<EditionInfo>()?.IsSelfHost == true
      ? SelfHostRefreshCookieName
      : HostedRefreshCookieName;

  /// <summary>Rolling browser-expiry for the refresh cookie on rotation (the server
  /// enforces the token's real lifetime; this just keeps the cookie alive).</summary>
  private const int RefreshCookieDays = 30;

  public AuthController(
    IAuthService authService,
    IDeviceTrustService deviceTrustService,
    ITwoFactorService twoFactorService,
    IStepUpAuthService stepUpAuthService,
    IIpGeolocationService geolocationService,
    IMfaPreferencesService mfaPreferencesService,
    IEmailService emailService,
    IRefreshTokenService refreshTokenService,
    IUserRepository userRepository,
    IConfiguration configuration,
    ILogger<AuthController> logger)
  {
    _authService = authService;
    _deviceTrustService = deviceTrustService;
    _twoFactorService = twoFactorService;
    _stepUpAuthService = stepUpAuthService;
    _geolocationService = geolocationService;
    _mfaPreferencesService = mfaPreferencesService;
    _emailService = emailService;
    _refreshTokenService = refreshTokenService;
    _userRepository = userRepository;
    _configuration = configuration;
    _logger = logger;
  }


  /// <summary>
  /// Sanctions/geo gate + signup-country capture (#941). Resolves the caller's
  /// country from their IP; returns a country code (null when geolocation is
  /// unavailable — we FAIL OPEN, a geo outage must never block signups) and
  /// whether service is blocked there (Sanctions:BlockedCountries; default OFAC
  /// comprehensive-sanctions baseline). Applied to all three account-creating
  /// endpoints; blocking covers login too — sanctioned-country service provision
  /// is the prohibited act, not just account creation.
  /// </summary>
  private async Task<(bool Blocked, string? CountryCode)> ResolveGeoGateAsync()
  {
    try
    {
      var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
      if (string.IsNullOrEmpty(ip)) return (false, null);

      var location = await _geolocationService.GetLocationAsync(ip);
      var country = location?.CountryCode;
      if (string.IsNullOrWhiteSpace(country)) return (false, null);
      country = country.ToUpperInvariant();

      var blocked = _configuration.GetSection("Sanctions:BlockedCountries").Get<string[]>()
        ?? new[] { "CU", "IR", "KP", "SY" };
      return (blocked.Contains(country, StringComparer.OrdinalIgnoreCase), country);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Geo gate resolution failed; failing open");
      return (false, null);
    }
  }

  /// <summary>Best-effort SignupCountry stamp on a newly created account.</summary>
  private async Task StampSignupCountryAsync(int userId, string? countryCode)
  {
    if (string.IsNullOrEmpty(countryCode)) return;
    try
    {
      var user = await _userRepository.GetByIdAsync(userId);
      if (user == null || user.SignupCountry != null) return;
      user.SignupCountry = countryCode;
      // Locale-default the budgeting week for NEW users only (#941): US/CA convention
      // is Sunday (null = the legacy Sunday behavior), everywhere else Monday (ISO 8601).
      if (user.WeekStartDay == null && countryCode != "US" && countryCode != "CA")
      {
        user.WeekStartDay = (int)DayOfWeek.Monday;
      }
      await _userRepository.UpdateAsync(user);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to stamp signup country for user {UserId}", userId);
    }
  }

  private ObjectResult GeoBlockedResult() =>
    StatusCode(StatusCodes.Status403Forbidden,
      new { message = "Buoy Money is not available in your region." });

  /// <summary>
  /// Issues a refresh token for the just-authenticated user, writes it to the
  /// response body (native clients read it there) and to an HttpOnly cookie (web
  /// clients use the cookie so the token is never exposed to JavaScript/XSS).
  /// </summary>
  private async Task IssueRefreshTokenAsync(LoginResponse? login)
  {
    if (login?.User == null || login.User.Id <= 0)
      return;
    var issued = await _refreshTokenService.IssueAsync(login.User.Id);
    login.RefreshToken = issued.Token;
    SetRefreshCookie(issued.Token, issued.ExpiresAt);
  }

  private void SetRefreshCookie(string token, DateTime expiresAt)
  {
    Response.Cookies.Append(RefreshCookieName, token, new CookieOptions
    {
      HttpOnly = true,
      // Secure over HTTPS (prod); relaxed on plain-HTTP localhost so dev still works.
      Secure = Request.IsHttps,
      SameSite = SameSiteMode.Lax,
      Expires = expiresAt,
      Path = "/",
      IsEssential = true
    });
  }

  private void ClearRefreshCookie()
  {
    Response.Cookies.Delete(RefreshCookieName, new CookieOptions
    {
      HttpOnly = true,
      Secure = Request.IsHttps,
      SameSite = SameSiteMode.Lax,
      Path = "/"
    });
  }

  /// <summary>Reads the refresh token from the HttpOnly cookie (web) or the request
  /// body (native clients).</summary>
  private string? ReadRefreshToken(string? bodyToken)
  {
    if (Request.Cookies.TryGetValue(RefreshCookieName, out var cookieToken) && !string.IsNullOrEmpty(cookieToken))
      return cookieToken;
    return bodyToken;
  }

  /// <summary>
  /// Enhanced login with device trust and risk-based MFA
  /// </summary>
  [HttpPost("login")]
  public async Task<IActionResult> Login([FromBody] EnhancedLoginRequest request)
  {
    if (PasswordLoginDisabled(out var passwordOff))
    {
      return passwordOff;
    }

    if (string.IsNullOrEmpty(request.Email) || string.IsNullOrEmpty(request.Password))
    {
      return BadRequest(new { message = "Email and password are required" });
    }

    // Get device information from request context
    var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
    var userAgent = Request.Headers["User-Agent"].ToString();

    var deviceInfo = new DeviceInfoDto
    {
      IpAddress = ipAddress,
      UserAgent = userAgent,
      DeviceFingerprint = request.DeviceFingerprint,
      RememberDevice = request.RememberDevice
    };

    // Attempt basic authentication first
    var basicLoginRequest = new LoginRequest
    {
      Email = request.Email,
      Password = request.Password
    };

    var user = await _authService.ValidateCredentialsAsync(basicLoginRequest);
    if (user == null)
    {
      // Log failed attempt if we can identify the user
      var existingUser = await _authService.GetUserByEmailAsync(request.Email);
      if (existingUser != null)
      {
        await _deviceTrustService.LogLoginAttemptAsync(
          existingUser.Id,
          deviceInfo,
          success: false,
          mfaChallenged: false,
          mfaSuccess: false,
          riskScore: 0,
          failureReason: "Invalid credentials");
      }

      return Unauthorized(new { message = "Invalid username or password" });
    }

    var (mfaChallenge, trustEvaluation) = await EvaluateMfaAsync(user, deviceInfo);
    if (mfaChallenge != null)
    {
      return Ok(mfaChallenge);
    }

    // No MFA required or user doesn't have MFA set up - proceed with login
    var loginResponse = await _authService.LoginAsync(basicLoginRequest);
    if (loginResponse == null)
    {
      return Unauthorized(new { message = "Login failed" });
    }

    // No second factor was completed on this path, so it must never create a trusted
    // device: trust is only granted after MFA succeeds (verify-mfa, passkey complete).
    // Otherwise a password-only login could pre-trust a device that would then skip
    // MFA once the user turns it on.
    if (trustEvaluation.TrustedDeviceId.HasValue)
    {
      // Update last used timestamp
      await _deviceTrustService.UpdateDeviceLastUsedAsync(trustEvaluation.TrustedDeviceId.Value);
    }

    // Log successful login
    await _deviceTrustService.LogLoginAttemptAsync(
      user.Id,
      deviceInfo,
      success: true,
      mfaChallenged: false,
      mfaSuccess: false,
      riskScore: trustEvaluation.RiskScore);

    await IssueRefreshTokenAsync(loginResponse);

    // Return full login response
    return Ok(new EnhancedLoginResponse
    {
      RequiresMfa = false,
      Token = loginResponse.Token,
      User = loginResponse.User,
      ExpiresAt = loginResponse.ExpiresAt,
      RefreshToken = loginResponse.RefreshToken,
      MfaReasons = new List<string>()
    });
  }

  /// <summary>
  /// The second-step decision shared by password and single sign-on: the user's MFA settings and
  /// the server-side trusted-device record decide. Returns the MFA challenge to send (and logs the
  /// attempt, and warns the user by email on a risky one), or null when no second step is needed.
  /// </summary>
  private async Task<(EnhancedLoginResponse? Challenge, DeviceTrustEvaluationDto Trust)> EvaluateMfaAsync(
    UserProfileDto user, DeviceInfoDto deviceInfo)
  {
    // Check if user has MFA enabled
    var mfaDetails = await _twoFactorService.GetUserMfaDetailsAsync(user.Id);

    // Evaluate device trust and risk-based MFA requirement
    var trustEvaluation = await _deviceTrustService.EvaluateDeviceTrustAsync(user.Id, deviceInfo);
    _logger.LogInformation(
      "Device trust evaluation: UserId={UserId}, HasMFA={HasMFA}, RequiresMFA={RequiresMFA}, RiskScore={RiskScore}",
      user.Id, mfaDetails.HasMfaEnabled, trustEvaluation.RequiresMfa, trustEvaluation.RiskScore);

    // Check if user has ANY usable MFA method
    bool hasTotpEnabled = mfaDetails.MfaType.Equals("totp", StringComparison.OrdinalIgnoreCase);
    bool hasEmailMfa = mfaDetails.MfaType.Equals("email", StringComparison.OrdinalIgnoreCase);
    bool hasUsableMfa = mfaDetails.HasPasskeyRegistered || hasTotpEnabled || hasEmailMfa;

    // The second step is decided only by facts the server can check: the user's MFA
    // settings and the server-side trusted-device record (EvaluateDeviceTrustAsync).
    // Nothing the client says about itself (e.g. the obsolete BiometricVerified flag,
    // which a local fingerprint unlock used to send) can skip the challenge. A known
    // device avoids repeat prompts only by having completed MFA with "remember device".
    bool requiresMfa = mfaDetails.HasMfaEnabled && trustEvaluation.RequiresMfa && hasUsableMfa;

    // Log warning if MFA would be required but user has no usable methods
    if (!hasUsableMfa && trustEvaluation.RequiresMfa && mfaDetails.HasMfaEnabled)
    {
      _logger.LogWarning(
        "User {UserId} triggered MFA requirement but has no usable MFA methods (MfaType: {MfaType}, TOTP: {HasTotp}, Passkey: {HasPasskey}). Allowing login.",
        user.Id, mfaDetails.MfaType, hasTotpEnabled, mfaDetails.HasPasskeyRegistered);
    }

    if (requiresMfa)
    {
      // Generate temporary MFA token (labels the challenge metric by the user's MFA type)
      var mfaToken = await _authService.GenerateMfaTokenAsync(user.Id, mfaDetails.MfaType);

      // Get user's preferred MFA method
      var preferredMethod = await _mfaPreferencesService.GetPreferredMfaMethodAsync(user.Id);

      // Determine available MFA methods for this user
      var availableMethods = new List<string>();
      if (mfaDetails.HasPasskeyRegistered) availableMethods.Add("Passkey");
      if (hasTotpEnabled) availableMethods.Add("Totp");
      if (hasEmailMfa) availableMethods.Add("Email");

      // Email fallback for passkey-ONLY accounts: a passkey can be unusable on the
      // current platform (e.g. a browser without WebAuthn support), which would
      // otherwise lock the user out with no second-factor they can complete. Offer
      // an emailed code as the fallback in that single case. We deliberately do NOT
      // add email for TOTP users — that would cap their MFA strength at email-level.
      // (send-login-mfa-code + CompleteMfaLoginAsync already accept an email code for
      // any user, so no other backend change is needed.)
      if (mfaDetails.HasPasskeyRegistered && !hasTotpEnabled && !hasEmailMfa
          && !availableMethods.Contains("Email"))
      {
        availableMethods.Add("Email");
      }

      // Log login attempt with MFA challenge
      await _deviceTrustService.LogLoginAttemptAsync(
        user.Id,
        deviceInfo,
        success: false, // Not yet successful - pending MFA
        mfaChallenged: true,
        mfaSuccess: false,
        riskScore: trustEvaluation.RiskScore);

      // Send suspicious login notification if risk score is medium or high
      if (trustEvaluation.RiskScore >= 40)
      {
        var mfaPreferences = await _mfaPreferencesService.GetPreferencesAsync(user.Id);
        if (mfaPreferences.NotifyOnSuspiciousLogin)
        {
          try
          {
            // Get geolocation for the notification
            var location = "Unknown Location";
            if (!string.IsNullOrEmpty(deviceInfo.IpAddress))
            {
              var geoLocation = await _geolocationService.GetLocationAsync(deviceInfo.IpAddress);
              if (geoLocation != null)
              {
                location = $"{geoLocation.City}, {geoLocation.Region}, {geoLocation.Country}";
              }
            }

            await _emailService.SendSuspiciousLoginAttemptAsync(
              user.Email,
              user.FirstName ?? "User",
              location,
              deviceInfo.IpAddress ?? "Unknown IP",
              trustEvaluation.RiskScore,
              DateTime.UtcNow
            );

            _logger.LogInformation("Sent suspicious login notification email to user {UserId} (Risk: {RiskScore})",
              user.Id, trustEvaluation.RiskScore);
          }
          catch (Exception ex)
          {
            _logger.LogError(ex, "Failed to send suspicious login notification email to user {UserId}", user.Id);
            // Don't fail the login if email fails
          }
        }
      }

      // Return response requiring MFA
      return (new EnhancedLoginResponse
      {
        RequiresMfa = true,
        MfaToken = mfaToken,
        PreferredMfaMethod = preferredMethod.ToString(),
        AvailableMfaMethods = availableMethods,
        MfaReasons = trustEvaluation.MfaReasons,
        Token = null,
        User = null,
        ExpiresAt = null
      }, trustEvaluation);
    }

    return (null, trustEvaluation);
  }

  /// <summary>
  /// Complete MFA challenge and obtain full authentication token
  /// </summary>
  [HttpPost("verify-mfa")]
  public async Task<IActionResult> VerifyMfa([FromBody] VerifyMfaLoginRequest request)
  {
    if (string.IsNullOrEmpty(request.MfaToken) || string.IsNullOrEmpty(request.MfaCode))
    {
      return BadRequest(new { message = "MFA token and code are required" });
    }

    // Get device information from request context
    var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
    var userAgent = Request.Headers["User-Agent"].ToString();

    var deviceInfo = new DeviceInfoDto
    {
      IpAddress = ipAddress,
      UserAgent = userAgent,
      DeviceFingerprint = request.DeviceFingerprint,
      RememberDevice = request.RememberDevice
    };

    // Complete MFA login
    var loginResponse = await _authService.CompleteMfaLoginAsync(request.MfaToken, request.MfaCode);
    if (loginResponse == null)
    {
      return Unauthorized(new { message = "Invalid or expired MFA code" });
    }

    // Register trusted device if requested
    if (request.RememberDevice && loginResponse.User != null)
    {
      await _deviceTrustService.RegisterTrustedDeviceAsync(loginResponse.User.Id, deviceInfo);

      // Check if user wants new device notifications
      var mfaPreferences = await _mfaPreferencesService.GetPreferencesAsync(loginResponse.User.Id);
      if (mfaPreferences.NotifyOnNewDevice)
      {
        try
        {
          // Get geolocation for the notification
          var location = "Unknown Location";
          if (!string.IsNullOrEmpty(deviceInfo.IpAddress))
          {
            var geoLocation = await _geolocationService.GetLocationAsync(deviceInfo.IpAddress);
            if (geoLocation != null)
            {
              location = $"{geoLocation.City}, {geoLocation.Region}, {geoLocation.Country}";
            }
          }

          var deviceName = !string.IsNullOrEmpty(deviceInfo.UserAgent) ? deviceInfo.UserAgent : "Unknown Device";
          await _emailService.SendNewDeviceLoginAsync(
            loginResponse.User.Email,
            loginResponse.User.FirstName ?? "User",
            deviceName,
            location,
            deviceInfo.IpAddress ?? "Unknown IP",
            DateTime.UtcNow
          );

          _logger.LogInformation("Sent new device notification email to user {UserId}", loginResponse.User.Id);
        }
        catch (Exception ex)
        {
          _logger.LogError(ex, "Failed to send new device notification email to user {UserId}", loginResponse.User.Id);
          // Don't fail the login if email fails
        }
      }
    }

    // Log successful MFA completion
    if (loginResponse.User != null)
    {
      var evaluation = await _deviceTrustService.EvaluateDeviceTrustAsync(loginResponse.User.Id, deviceInfo);
      await _deviceTrustService.LogLoginAttemptAsync(
        loginResponse.User.Id,
        deviceInfo,
        success: true,
        mfaChallenged: true,
        mfaSuccess: true,
        riskScore: evaluation.RiskScore);
    }

    await IssueRefreshTokenAsync(loginResponse);

    return Ok(new EnhancedLoginResponse
    {
      RequiresMfa = false,
      Token = loginResponse.Token,
      User = loginResponse.User,
      ExpiresAt = loginResponse.ExpiresAt,
      RefreshToken = loginResponse.RefreshToken,
      MfaReasons = new List<string>()
    });
  }

  /// <summary>
  /// Send MFA code for login verification (Email method)
  /// </summary>
  [HttpPost("send-login-mfa-code")]
  public async Task<IActionResult> SendLoginMfaCode([FromBody] SendLoginMfaCodeRequest request)
  {
    if (string.IsNullOrEmpty(request.MfaToken))
    {
      return BadRequest(new { message = "MFA token is required" });
    }

    // FULLY validate the MFA token (signature/issuer/audience/lifetime + mfa_pending
    // claim) — not just decode it. Merely reading the JWT let an unauthenticated attacker
    // forge a token for any user id and use this endpoint as an email-send oracle.
    var validatedUserId = _authService.ValidateMfaToken(request.MfaToken);
    if (validatedUserId == null)
    {
      return Unauthorized(new { message = "Invalid MFA token" });
    }

    var userId = validatedUserId.Value;

    // Get device information for logging
    var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
    var userAgent = Request.Headers["User-Agent"].ToString();

    // Generate and send email code. A forged-but-signed token can't reach here, but a
    // token whose user no longer exists throws "User not found" from the 2FA service —
    // swallow it and return the SAME generic 200 so this endpoint can't be used as an
    // account-existence (userId) enumeration oracle.
    try
    {
      await _twoFactorService.GenerateAndSendCodeAsync(
        userId,
        "login_verification",
        ipAddress,
        userAgent);

      _logger.LogInformation("Sent login MFA code to user {UserId} via email", userId);
    }
    catch (ArgumentException ex)
    {
      _logger.LogWarning(ex, "send-login-mfa-code for a non-existent user id; returning generic response");
    }

    return Ok(new { message = "MFA code sent successfully" });
  }

  [HttpGet("check-email/{email}")]
  public async Task<IActionResult> CheckEmailExists(string email)
  {
    if (string.IsNullOrEmpty(email))
    {
      return BadRequest(new { message = "Email is required" });
    }

    var user = await _authService.GetUserByEmailAsync(email);
    return Ok(new { exists = user != null });
  }

  [HttpPost("register")]
  public async Task<IActionResult> Register([FromBody] RegisterRequest request)
  {
    if (PasswordLoginDisabled(out var passwordOff))
    {
      return passwordOff;
    }

    if (string.IsNullOrEmpty(request.Email) || string.IsNullOrEmpty(request.Password) ||
        string.IsNullOrEmpty(request.FirstName) || string.IsNullOrEmpty(request.LastName))
    {
      return BadRequest(new { message = "All fields are required" });
    }

    // Surface the specific password problem instead of a generic failure (otherwise a
    // weak password reads as "email already in use").
    var passwordValidation = PasswordValidationService.ValidatePassword(request.Password);
    if (!passwordValidation.IsValid)
    {
      return BadRequest(new
      {
        message = string.Join(" ", passwordValidation.Errors),
        errors = passwordValidation.Errors
      });
    }

    var (geoBlocked, signupCountry) = await ResolveGeoGateAsync();
    if (geoBlocked) return GeoBlockedResult();

    LoginResponse? response;
    try
    {
      response = await _authService.RegisterAsync(request);
    }
    catch (RegistrationClosedException ex)
    {
      return StatusCode(403, new { message = ex.Message, code = "registration_closed" });
    }
    if (response == null)
    {
      // Password/format were already validated above, so the remaining cause is a taken
      // email (or a concurrent signup).
      return BadRequest(new { message = "This email is already registered. Try signing in instead." });
    }

    await StampSignupCountryAsync(response.User.Id, signupCountry);
    await IssueRefreshTokenAsync(response);
    return Ok(response);
  }

  [HttpPost("google-login")]
  public async Task<IActionResult> GoogleLogin([FromBody] GoogleLoginRequest request)
  {
    // Self-host: Google sign-in is off until the operator sets up their own Google client.
    if (HttpContext.RequestServices.GetService<EditionInfo>()?.IsSelfHost == true &&
        string.IsNullOrWhiteSpace(_configuration["GoogleAuth:ClientId"]))
    {
      return NotFound(new { message = "Google sign-in is not set up on this server." });
    }

    if (string.IsNullOrEmpty(request.IdToken))
    {
      return BadRequest(new { message = "Google ID token is required" });
    }

    var (geoBlocked, signupCountry) = await ResolveGeoGateAsync();
    if (geoBlocked) return GeoBlockedResult();

    try
    {
      var response = await _authService.GoogleLoginAsync(request);
      if (response == null)
      {
        return Unauthorized(new { message = "Invalid Google token" });
      }

      if (response.IsNewUser)
      {
        await StampSignupCountryAsync(response.User.Id, signupCountry);
      }
      await IssueRefreshTokenAsync(response);
      return Ok(response);
    }
    catch (GoogleAccountNotFoundException)
    {
      // Login flow with no matching account. Deliberately still a 404: the Flutter client
      // keys its redirect-to-register on this exact status, and installed apps in the field
      // would fall through to a generic error toast if it moved. This is the ONE benign
      // outcome on this endpoint, so alerting should exclude 404 rather than reclassify it.
      return NotFound(new { message = "No account exists for this Google email. Please sign up first.", code = "no_account" });
    }
    catch (GoogleRegistrationRejectedException ex)
    {
      // Previously both of these returned a bare null from the service and surfaced as 401
      // "Invalid Google token", making a throwaway-domain signup and a broken token audience
      // indistinguishable on the wire. They are now separable in metrics:
      //   403 = refused on purpose, no action needed
      //   429 = transient pressure, retry works
      //   401 = the token itself did not validate, which is the one worth paging on
      return ex.Reason switch
      {
        GoogleRegistrationRejection.RateLimited =>
          StatusCode(429, new { message = ex.Message, code = "rate_limited" }),
        GoogleRegistrationRejection.RegistrationClosed =>
          StatusCode(403, new { message = ex.Message, code = "registration_closed" }),
        _ =>
          StatusCode(403, new { message = ex.Message, code = "blocked_domain" }),
      };
    }
  }

  [HttpPost("apple-login")]
  public async Task<IActionResult> AppleLogin([FromBody] AppleLoginRequest request)
  {
    // Self-host: Apple tokens are minted for BuoyAI's own app, so they must not sign anyone in here.
    if (HttpContext.RequestServices.GetService<EditionInfo>()?.IsSelfHost == true)
    {
      return NotFound(new { message = "Apple sign-in is not available on this server." });
    }

    if (string.IsNullOrEmpty(request.IdentityToken))
    {
      return BadRequest(new { message = "Apple identity token is required" });
    }

    // NOTE: do not require request.UserIdentifier — Apple's web (JS SDK) flow does not
    // return one; the authoritative user id is the verified token's `sub`, which
    // AppleLoginAsync reads. Native sends a userIdentifier too, but it's not trusted.

    var (geoBlocked, signupCountry) = await ResolveGeoGateAsync();
    if (geoBlocked) return GeoBlockedResult();

    var response = await _authService.AppleLoginAsync(request);
    if (response == null)
    {
      return Unauthorized(new { message = "Invalid Apple credentials" });
    }

    if (response.IsNewUser)
    {
      await StampSignupCountryAsync(response.User.Id, signupCountry);
    }
    await IssueRefreshTokenAsync(response);
    return Ok(response);
  }

  private bool IsSelfHostEdition => HttpContext.RequestServices?.GetService<EditionInfo>()?.IsSelfHost == true;

  /// <summary>The origin this request came in on, used only when App:BaseUrl is not set.</summary>
  private string RequestOrigin => $"{Request.Scheme}://{Request.Host}";

  /// <summary>
  /// A self-hosted operator can turn password sign-in off in favour of single sign-on
  /// (Oidc:DisablePasswordLogin). It only takes effect while single sign-on is configured, and
  /// setting the flag false again (BUOY_OIDC_DISABLE_PASSWORD_LOGIN=false) brings passwords back.
  /// </summary>
  private bool PasswordLoginDisabled(out IActionResult result)
  {
    result = null!;
    if (!IsSelfHostEdition) return false;
    var options = OidcOptions.From(_configuration);
    if (!options.PasswordLoginDisabled) return false;
    result = StatusCode(StatusCodes.Status403Forbidden, new
    {
      message = $"Password sign-in is turned off on this server. Use {options.DisplayName}.",
      code = "password_login_disabled"
    });
    return true;
  }

  /// <summary>
  /// Single sign-on, step 1 (self-host): sends the browser to the operator's OpenID Connect
  /// provider. <paramref name="platform"/> is "web" (back to /app/auth/oidc) or "mobile" (back to
  /// the app's com.buoyai.buoymoney://oidc deep link; requires the app's own PKCE challenge).
  /// 404 on hosted and when single sign-on is not set up.
  /// </summary>
  [AllowAnonymous]
  [HttpGet("oidc/start")]
  public async Task<IActionResult> OidcStart(
    [FromServices] OidcLoginService oidc,
    [FromQuery] string? platform,
    [FromQuery] string? redirect,
    [FromQuery] string? invite,
    [FromQuery(Name = "code_challenge")] string? codeChallenge)
  {
    if (!IsSelfHostEdition || !oidc.Options.IsConfigured) return NotFound();
    Response.Headers.CacheControl = "no-store";
    try
    {
      var url = await oidc.StartAsync(platform, redirect, invite, codeChallenge, RequestOrigin, HttpContext.RequestAborted);
      return Redirect(url);
    }
    catch (OidcLoginException ex)
    {
      _logger.LogWarning("Single sign-on could not start: {Code} {Message}", ex.Code, ex.Message);
      var target = OidcLoginService.NormalizePlatform(platform) ?? OidcLoginService.PlatformWeb;
      return Redirect(oidc.ClientRedirect(target, RequestOrigin, null, ex.Code, null));
    }
  }

  /// <summary>
  /// Single sign-on, step 2: the provider sends the browser back here. Verifies the answer, finds
  /// or creates the account, then sends the browser to the app with a one-time code (never a token).
  /// </summary>
  [AllowAnonymous]
  [HttpGet("oidc/callback")]
  public async Task<IActionResult> OidcCallback(
    [FromServices] OidcLoginService oidc,
    [FromQuery] string? code,
    [FromQuery] string? state,
    [FromQuery] string? error)
  {
    if (!IsSelfHostEdition || !oidc.Options.IsConfigured) return NotFound();
    var url = await oidc.CallbackAsync(state, code, error, RequestOrigin, HttpContext.RequestAborted);
    Response.Headers.CacheControl = "no-store";
    Response.Headers["Referrer-Policy"] = "no-referrer";
    return Redirect(url);
  }

  /// <summary>
  /// Single sign-on, step 3: the app trades the one-time code (single use, 60 seconds) for the
  /// normal sign-in answer. The app's own MFA still applies unless Oidc:SkipAppMfa is on, so the
  /// answer can be an MFA challenge exactly like a password sign-in.
  /// </summary>
  [AllowAnonymous]
  [HttpPost("oidc/exchange")]
  public async Task<IActionResult> OidcExchange([FromServices] OidcLoginService oidc, [FromBody] OidcExchangeRequest request)
  {
    var options = oidc.Options;
    if (!IsSelfHostEdition || !options.IsConfigured) return NotFound();

    var (geoBlocked, _) = await ResolveGeoGateAsync();
    if (geoBlocked) return GeoBlockedResult();

    OidcResolvedUser resolved;
    try
    {
      resolved = await oidc.ExchangeAsync(request.Code, request.CodeVerifier, HttpContext.RequestAborted);
    }
    catch (OidcLoginException ex)
    {
      return BadRequest(new { message = ex.Message, code = ex.Code });
    }

    var deviceInfo = new DeviceInfoDto
    {
      IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
      UserAgent = Request.Headers["User-Agent"].ToString(),
      DeviceFingerprint = request.DeviceFingerprint,
      RememberDevice = request.RememberDevice
    };

    var riskScore = 0;
    if (!options.SkipAppMfa)
    {
      var profile = await _authService.GetUserProfileAsync(resolved.UserId);
      if (profile == null)
      {
        return Unauthorized(new { message = "This account is no longer available." });
      }
      var (challenge, trust) = await EvaluateMfaAsync(profile, deviceInfo);
      if (challenge != null)
      {
        return Ok(challenge);
      }
      riskScore = trust.RiskScore;
      if (trust.TrustedDeviceId.HasValue)
      {
        await _deviceTrustService.UpdateDeviceLastUsedAsync(trust.TrustedDeviceId.Value);
      }
    }

    var login = await _authService.IssueLoginForUserAsync(resolved.UserId, resolved.IsNewUser);
    if (login == null)
    {
      return Unauthorized(new { message = "This account is no longer available." });
    }

    await _deviceTrustService.LogLoginAttemptAsync(
      resolved.UserId,
      deviceInfo,
      success: true,
      mfaChallenged: false,
      mfaSuccess: false,
      riskScore: riskScore);

    await IssueRefreshTokenAsync(login);
    return Ok(new EnhancedLoginResponse
    {
      RequiresMfa = false,
      Token = login.Token,
      User = login.User,
      ExpiresAt = login.ExpiresAt,
      RefreshToken = login.RefreshToken,
      MfaReasons = new List<string>(),
      IsNewUser = login.IsNewUser
    });
  }

  /// <summary>
  /// Refresh the access token by rotating the refresh token. Reads the refresh token
  /// from the HttpOnly cookie (web) or the request body (native). Returns a new access
  /// token; the rotated refresh token is set back on the cookie and in the body.
  /// </summary>
  [AllowAnonymous]
  [HttpPost("refresh")]
  public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequest? request)
  {
    var refreshToken = ReadRefreshToken(request?.RefreshToken);
    if (string.IsNullOrEmpty(refreshToken))
    {
      return Unauthorized(new { message = "No refresh token supplied" });
    }

    var result = await _authService.RefreshTokenAsync(refreshToken);
    if (result == null)
    {
      // Invalid / expired / revoked / reused — force the client back to login.
      ClearRefreshCookie();
      return Unauthorized(new { message = "Invalid or expired refresh token" });
    }

    if (!string.IsNullOrEmpty(result.RefreshToken))
    {
      // The rolling cookie window matches the refresh-token lifetime; the server still
      // enforces the token's own hard expiry regardless of the cookie.
      SetRefreshCookie(result.RefreshToken, DateTime.UtcNow.AddDays(RefreshCookieDays));
    }

    return Ok(result);
  }

  /// <summary>
  /// Log out: revoke the presented refresh token and clear the cookie.
  /// </summary>
  [AllowAnonymous]
  [HttpPost("logout")]
  public async Task<IActionResult> Logout([FromBody] RefreshTokenRequest? request)
  {
    var refreshToken = ReadRefreshToken(request?.RefreshToken);
    if (!string.IsNullOrEmpty(refreshToken))
    {
      await _refreshTokenService.RevokeAsync(refreshToken);
    }
    ClearRefreshCookie();
    return Ok(new { message = "Logged out" });
  }

  /// <summary>
  /// Lightweight password check for the current user. Lets the app confirm a password (e.g. to
  /// enable quick-login) WITHOUT going through /login — it mints no tokens, creates no session,
  /// and does not rotate refresh tokens, so it doesn't burn the auth flow or spawn throwaway
  /// tokens. Inherits the class-level "auth" rate-limit so it can't be brute-forced. Returns
  /// 200 { valid } for both outcomes; accounts with no password (Google/Apple/MFA-only) get
  /// valid:false gracefully.
  /// </summary>
  [HttpPost("verify-password")]
  [Authorize]
  public async Task<IActionResult> VerifyPassword([FromBody] VerifyPasswordRequest request)
  {
    if (request == null || string.IsNullOrEmpty(request.Password))
    {
      return BadRequest(new { message = "Password is required" });
    }

    var userId = GetCurrentUserId();
    var valid = await _authService.VerifyPasswordAsync(userId, request.Password);
    return Ok(new VerifyPasswordResponse { Valid = valid });
  }

  /// <summary>
  /// Request a password reset email
  /// </summary>
  /// <remarks>
  /// Always returns success to prevent email enumeration attacks.
  /// If the email exists, a reset link will be sent.
  /// </remarks>
  [HttpPost("request-password-reset")]
  [AllowAnonymous]
  public async Task<IActionResult> RequestPasswordReset([FromBody] RequestPasswordResetRequest request)
  {
    if (string.IsNullOrWhiteSpace(request.Email))
    {
      return BadRequest(new { message = "Email is required" });
    }

    var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
    var userAgent = Request.Headers["User-Agent"].ToString();

    await _authService.RequestPasswordResetAsync(request.Email, ipAddress, userAgent);

    // IMPORTANT: Always return success to prevent email enumeration
    return Ok(new {
      message = "If an account exists with that email, a password reset link has been sent. Please check your inbox."
    });
  }

  /// <summary>
  /// Validate a password reset token
  /// </summary>
  [HttpGet("validate-reset-token")]
  [AllowAnonymous]
  public async Task<IActionResult> ValidateResetToken([FromQuery] string token)
  {
    if (string.IsNullOrWhiteSpace(token))
    {
      return BadRequest(new { message = "Token is required", isValid = false });
    }

    var isValid = await _authService.ValidatePasswordResetTokenAsync(token);

    return Ok(new { isValid });
  }

  /// <summary>
  /// Reset password using token and new password
  /// </summary>
  [HttpPost("reset-password")]
  [AllowAnonymous]
  public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
  {
    if (string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.NewPassword))
    {
      return BadRequest(new { message = "Token and new password are required" });
    }

    var success = await _authService.ResetPasswordAsync(request.Token, request.NewPassword);

    if (!success)
    {
      return BadRequest(new {
        message = "Invalid or expired token, or password does not meet security requirements"
      });
    }

    return Ok(new {
      message = "Password successfully reset. You can now log in with your new password."
    });
  }

  /// <summary>
  /// Request step-up MFA for a protected operation (Tier 2 & 3)
  /// </summary>
  [HttpPost("step-up-mfa/request")]
  [Authorize]
  public async Task<IActionResult> RequestStepUpMfa([FromBody] VerifyStepUpMfaRequest request)
  {
    if (!Enum.TryParse<Core.Enums.ProtectedOperation>(request.Operation, out var protectedOp))
    {
      return BadRequest(new { message = "Valid operation is required" });
    }

    var userId = GetCurrentUserId();
    var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
    var userAgent = Request.Headers["User-Agent"].ToString();

    // Send MFA code for the operation
    await _stepUpAuthService.SendStepUpMfaCodeAsync(
      userId,
      protectedOp,
      ipAddress,
      userAgent);

    return Ok(new
    {
      message = "MFA code sent successfully",
      operation = protectedOp.ToString(),
      requirementLevel = _stepUpAuthService.GetRequirementLevel(protectedOp).ToString()
    });
  }

  /// <summary>
  /// Verify step-up MFA code and get operation token
  /// </summary>
  [HttpPost("step-up-mfa/verify")]
  [Authorize]
  public async Task<IActionResult> VerifyStepUpMfa([FromBody] VerifyStepUpMfaRequest request)
  {
    if (string.IsNullOrEmpty(request.MfaCode))
    {
      return BadRequest(new { message = "MFA code is required" });
    }

    if (!Enum.TryParse<Core.Enums.ProtectedOperation>(request.Operation, out var protectedOp))
    {
      return BadRequest(new { message = "Valid operation is required" });
    }

    var userId = GetCurrentUserId();

    // Verify the MFA code and get operation token
    var response = await _stepUpAuthService.VerifyStepUpMfaAsync(
      userId,
      request.MfaCode,
      protectedOp);

    if (response == null)
    {
      // 403, not 401: the user's session is still valid — only the step-up factor
      // failed. A 401 would trip the client's auto-logout interceptor and sign the
      // user out of the app entirely on a single wrong code.
      return StatusCode(StatusCodes.Status403Forbidden,
        new { error = "StepUpVerificationFailed", message = "Invalid or expired MFA code" });
    }

    return Ok(response);
  }

  /// <summary>
  /// Check if an operation requires MFA
  /// </summary>
  [HttpGet("step-up-mfa/check")]
  [Authorize]
  public async Task<IActionResult> CheckMfaRequirement([FromQuery] string operation)
  {
    if (string.IsNullOrEmpty(operation) || !Enum.TryParse<Core.Enums.ProtectedOperation>(operation, out var protectedOp))
    {
      return BadRequest(new { message = "Valid operation is required" });
    }

    var userId = GetCurrentUserId();
    var requiresMfa = await _stepUpAuthService.RequiresMfaForOperationAsync(userId, protectedOp);
    var requirementLevel = _stepUpAuthService.GetRequirementLevel(protectedOp);

    return Ok(new
    {
      requiresMfa,
      operation = protectedOp.ToString(),
      requirementLevel = requirementLevel.ToString()
    });
  }

  [HttpPost("step-up-mfa/passkey/begin")]
  [Authorize]
  public async Task<IActionResult> BeginStepUpPasskey([FromBody] VerifyStepUpMfaRequest request)
  {
    if (!Enum.TryParse<Core.Enums.ProtectedOperation>(request.Operation, out _))
    {
      return BadRequest(new { message = "Valid operation is required" });
    }

    // Scope the ceremony to this authenticated user's registered credentials so the
    // authenticator returns one of OUR passkeys, not an arbitrary discoverable one.
    var options = await _twoFactorService.InitiatePasskeyAuthenticationAsync(
      new PasskeyAuthenticationInitDto { UserId = GetCurrentUserId() });

    return Ok(options);
  }

  [HttpPost("step-up-mfa/passkey/verify")]
  [Authorize]
  public async Task<IActionResult> VerifyStepUpPasskey([FromBody] StepUpPasskeyVerifyRequest request)
  {
    if (string.IsNullOrEmpty(request.AssertionResponse))
    {
      return BadRequest(new { message = "Assertion response is required" });
    }

    if (!Enum.TryParse<Core.Enums.ProtectedOperation>(request.Operation, out var protectedOp))
    {
      return BadRequest(new { message = "Valid operation is required" });
    }

    var userId = GetCurrentUserId();
    var response = await _stepUpAuthService.VerifyStepUpPasskeyAsync(
      userId, request.AssertionResponse, protectedOp);

    if (response == null)
    {
      // 403, not 401: the session is valid; only the step-up passkey check failed.
      // Returning 401 would trigger the client's auto-logout interceptor and sign the
      // user out completely (e.g. mid bank-link flow) instead of just failing the step-up.
      return StatusCode(StatusCodes.Status403Forbidden,
        new { error = "StepUpVerificationFailed", message = "Passkey verification failed" });
    }

    return Ok(response);
  }
}
