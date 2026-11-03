using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using BuoyAI.API.Controllers;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.UnitTests.Controllers;

public class AuthControllerTests
{
  private readonly Mock<IAuthService> _mockAuthService;
  private readonly Mock<IDeviceTrustService> _mockDeviceTrustService;
  private readonly Mock<ITwoFactorService> _mockTwoFactorService;
  private readonly Mock<IStepUpAuthService> _mockStepUpAuthService;
  private readonly Mock<IIpGeolocationService> _mockGeolocationService;
  private readonly Mock<IMfaPreferencesService> _mockMfaPreferencesService;
  private readonly Mock<IEmailService> _mockEmailService;
  private readonly Mock<IRefreshTokenService> _mockRefreshTokenService;
  private readonly Mock<ILogger<AuthController>> _mockLogger;
  private readonly AuthController _controller;

  public AuthControllerTests()
  {
    _mockAuthService = new Mock<IAuthService>();
    _mockDeviceTrustService = new Mock<IDeviceTrustService>();
    _mockTwoFactorService = new Mock<ITwoFactorService>();
    _mockStepUpAuthService = new Mock<IStepUpAuthService>();
    _mockGeolocationService = new Mock<IIpGeolocationService>();
    _mockMfaPreferencesService = new Mock<IMfaPreferencesService>();
    _mockEmailService = new Mock<IEmailService>();
    _mockRefreshTokenService = new Mock<IRefreshTokenService>();
    _mockRefreshTokenService
      .Setup(x => x.IssueAsync(It.IsAny<int>(), It.IsAny<Guid?>()))
      .ReturnsAsync(new IssuedRefreshToken("test-refresh-token", DateTime.UtcNow.AddDays(30), Guid.NewGuid()));
    _mockLogger = new Mock<ILogger<AuthController>>();
    _controller = new AuthController(
      _mockAuthService.Object,
      _mockDeviceTrustService.Object,
      _mockTwoFactorService.Object,
      _mockStepUpAuthService.Object,
      _mockGeolocationService.Object,
      _mockMfaPreferencesService.Object,
      _mockEmailService.Object,
      _mockRefreshTokenService.Object,
      new Mock<IUserRepository>().Object,
      new ConfigurationBuilder().Build(),
      _mockLogger.Object);
      
    // Setup HttpContext for the controller
    var httpContext = new DefaultHttpContext();
    httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("127.0.0.1");
    httpContext.Request.Headers["User-Agent"] = "TestUserAgent/1.0";
    _controller.ControllerContext = new ControllerContext
    {
      HttpContext = httpContext
    };
  }

  /// <summary>Attaches an authenticated principal so GetCurrentUserId() resolves to <paramref name="userId"/>.</summary>
  private void SetAuthenticatedUser(int userId)
  {
    var identity = new ClaimsIdentity(new[]
    {
      new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
    }, "TestAuth");
    _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(identity);
  }

  [Fact]
  public async Task Login_WithValidCredentials_ShouldReturnOkWithToken()
  {
    // Arrange
    var enhancedLoginRequest = new EnhancedLoginRequest
    {
      Email = "test@example.com",
      Password = "password123",
      RememberDevice = false
    };

    var userProfile = new UserProfileDto
    {
      Id = 1,
      Username = "testuser",
      Email = "test@example.com",
      FirstName = "Test",
      LastName = "User",
      Currency = "USD"
    };

    var loginResponse = new LoginResponse
    {
      Token = "jwt-token",
      User = userProfile,
      ExpiresAt = DateTime.UtcNow.AddHours(24)
    };

    var basicRequest = new LoginRequest { Email = enhancedLoginRequest.Email, Password = enhancedLoginRequest.Password };
    _mockAuthService.Setup(x => x.ValidateCredentialsAsync(It.IsAny<LoginRequest>()))
      .ReturnsAsync(userProfile);
    
    _mockTwoFactorService.Setup(x => x.GetUserMfaDetailsAsync(It.IsAny<int>()))
      .ReturnsAsync(new UserMfaDetailsDto { HasMfaEnabled = false, HasPasskeyRegistered = false, MfaType = "none" });
    
    _mockDeviceTrustService.Setup(x => x.EvaluateDeviceTrustAsync(It.IsAny<int>(), It.IsAny<DeviceInfoDto>()))
      .ReturnsAsync(new DeviceTrustEvaluationDto { RequiresMfa = false, RiskScore = 0, MfaReasons = new List<string>() });
    
    _mockDeviceTrustService.Setup(x => x.LogLoginAttemptAsync(It.IsAny<int>(), It.IsAny<DeviceInfoDto>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<string>()))
      .Returns(Task.CompletedTask);
    
    _mockAuthService.Setup(x => x.LoginAsync(It.IsAny<LoginRequest>()))
      .ReturnsAsync(loginResponse);

    // Act
    var result = await _controller.Login(enhancedLoginRequest);

    // Assert
    var objectResult = result.Should().BeOfType<OkObjectResult>().Subject;
    objectResult.StatusCode.Should().Be(200);
    var returnedResponse = objectResult.Value.Should().BeOfType<EnhancedLoginResponse>().Subject;
    returnedResponse.RequiresMfa.Should().BeFalse();
    returnedResponse.Token.Should().Be("jwt-token");
    returnedResponse.User?.Username.Should().Be("testuser");
  }

  private static readonly UserProfileDto MfaUser = new()
  {
    Id = 42,
    Username = "mfauser",
    Email = "mfa@example.com",
    FirstName = "Mfa",
    LastName = "User",
    Currency = "USD"
  };

  /// <summary>A valid password for a user with TOTP on, plus whatever the server-side
  /// trusted-device record says about this device.</summary>
  private void ArrangeMfaUserLogin(DeviceTrustEvaluationDto trust)
  {
    _mockAuthService.Setup(x => x.ValidateCredentialsAsync(It.IsAny<LoginRequest>()))
      .ReturnsAsync(MfaUser);
    _mockTwoFactorService.Setup(x => x.GetUserMfaDetailsAsync(MfaUser.Id))
      .ReturnsAsync(new UserMfaDetailsDto { HasMfaEnabled = true, HasPasskeyRegistered = false, MfaType = "totp" });
    _mockDeviceTrustService.Setup(x => x.EvaluateDeviceTrustAsync(MfaUser.Id, It.IsAny<DeviceInfoDto>()))
      .ReturnsAsync(trust);
    _mockAuthService.Setup(x => x.GenerateMfaTokenAsync(MfaUser.Id, It.IsAny<string>()))
      .ReturnsAsync("mfa-pending-token");
    _mockMfaPreferencesService.Setup(x => x.GetPreferredMfaMethodAsync(MfaUser.Id))
      .ReturnsAsync(BuoyAI.Core.Enums.MfaType.Totp);
    _mockAuthService.Setup(x => x.LoginAsync(It.IsAny<LoginRequest>()))
      .ReturnsAsync(new LoginResponse { Token = "jwt-token", User = MfaUser, ExpiresAt = DateTime.UtcNow.AddHours(24) });
  }

  [Fact]
  public async Task Login_ClientClaimsBiometric_MfaUserOnUntrustedDevice_GetsMfaChallengeNotTokens()
  {
    ArrangeMfaUserLogin(new DeviceTrustEvaluationDto
    {
      IsTrustedDevice = false,
      RequiresMfa = true,
      RiskScore = 0,
      MfaReasons = new List<string> { "New or untrusted device" }
    });

#pragma warning disable CS0618 // the obsolete flag is exactly what this test sends
    var request = new EnhancedLoginRequest
    {
      Email = MfaUser.Email,
      Password = "right-password",
      BiometricVerified = true,
      RememberDevice = true
    };
#pragma warning restore CS0618

    var result = await _controller.Login(request);

    var response = result.Should().BeOfType<OkObjectResult>().Subject
      .Value.Should().BeOfType<EnhancedLoginResponse>().Subject;
    response.RequiresMfa.Should().BeTrue();
    response.MfaToken.Should().Be("mfa-pending-token");
    response.Token.Should().BeNull();
    response.RefreshToken.Should().BeNull();
    response.User.Should().BeNull();
    _mockAuthService.Verify(x => x.LoginAsync(It.IsAny<LoginRequest>()), Times.Never);
    _mockRefreshTokenService.Verify(x => x.IssueAsync(It.IsAny<int>(), It.IsAny<Guid?>()), Times.Never);
    _mockDeviceTrustService.Verify(x => x.RegisterTrustedDeviceAsync(It.IsAny<int>(), It.IsAny<DeviceInfoDto>()), Times.Never);
  }

  [Fact]
  public async Task Login_MfaUserOnServerTrustedDevice_SkipsChallenge()
  {
    ArrangeMfaUserLogin(new DeviceTrustEvaluationDto
    {
      IsTrustedDevice = true,
      TrustedDeviceId = 7,
      RequiresMfa = false,
      RiskScore = 0,
      MfaReasons = new List<string>()
    });

    var result = await _controller.Login(new EnhancedLoginRequest
    {
      Email = MfaUser.Email,
      Password = "right-password"
    });

    var response = result.Should().BeOfType<OkObjectResult>().Subject
      .Value.Should().BeOfType<EnhancedLoginResponse>().Subject;
    response.RequiresMfa.Should().BeFalse();
    response.Token.Should().Be("jwt-token");
    response.RefreshToken.Should().Be("test-refresh-token");
    _mockDeviceTrustService.Verify(x => x.UpdateDeviceLastUsedAsync(7), Times.Once);
    _mockAuthService.Verify(x => x.GenerateMfaTokenAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
  }

  [Fact]
  public async Task Login_PasswordOnlyWithRememberDevice_NeverCreatesTrustedDevice()
  {
    // A user without MFA logs in from a new device and asks to be remembered. No
    // second factor happened, so the device must not become trusted; otherwise it
    // would skip MFA after the user turns MFA on.
    var user = new UserProfileDto { Id = 5, Username = "nomfa", Email = "nomfa@example.com", FirstName = "No", LastName = "Mfa", Currency = "USD" };
    _mockAuthService.Setup(x => x.ValidateCredentialsAsync(It.IsAny<LoginRequest>()))
      .ReturnsAsync(user);
    _mockTwoFactorService.Setup(x => x.GetUserMfaDetailsAsync(user.Id))
      .ReturnsAsync(new UserMfaDetailsDto { HasMfaEnabled = false, MfaType = "none" });
    _mockDeviceTrustService.Setup(x => x.EvaluateDeviceTrustAsync(user.Id, It.IsAny<DeviceInfoDto>()))
      .ReturnsAsync(new DeviceTrustEvaluationDto { RequiresMfa = true, RiskScore = 0, MfaReasons = new List<string> { "New or untrusted device" } });
    _mockAuthService.Setup(x => x.LoginAsync(It.IsAny<LoginRequest>()))
      .ReturnsAsync(new LoginResponse { Token = "jwt-token", User = user, ExpiresAt = DateTime.UtcNow.AddHours(24) });

    var result = await _controller.Login(new EnhancedLoginRequest
    {
      Email = user.Email,
      Password = "right-password",
      RememberDevice = true
    });

    var response = result.Should().BeOfType<OkObjectResult>().Subject
      .Value.Should().BeOfType<EnhancedLoginResponse>().Subject;
    response.RequiresMfa.Should().BeFalse();
    response.Token.Should().Be("jwt-token");
    _mockDeviceTrustService.Verify(x => x.RegisterTrustedDeviceAsync(It.IsAny<int>(), It.IsAny<DeviceInfoDto>()), Times.Never);
  }

  [Fact]
  public async Task Login_WithInvalidCredentials_ShouldReturnUnauthorized()
  {
    // Arrange
    var enhancedLoginRequest = new EnhancedLoginRequest
    {
      Email = "test@example.com",
      Password = "wrongpassword"
    };

    _mockAuthService.Setup(x => x.ValidateCredentialsAsync(It.IsAny<LoginRequest>()))
      .ReturnsAsync((UserProfileDto?)null);
      
    _mockAuthService.Setup(x => x.GetUserByEmailAsync(It.IsAny<string>()))
      .ReturnsAsync((UserProfileDto?)null);

    // Act
    var result = await _controller.Login(enhancedLoginRequest);

    // Assert  
    var objectResult = result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
    objectResult.StatusCode.Should().Be(401);
  }

  [Fact]
  public async Task Login_WithEmptyEmail_ShouldReturnBadRequest()
  {
    // Arrange
    var enhancedLoginRequest = new EnhancedLoginRequest
    {
      Email = "",
      Password = "password123"
    };

    // Act
    var result = await _controller.Login(enhancedLoginRequest);

    // Assert
    var badRequestResult = result.Should().BeOfType<BadRequestObjectResult>().Subject;
    var responseBody = badRequestResult.Value.Should().BeEquivalentTo(new { message = "Email and password are required" });
  }

  [Fact]
  public async Task Login_WithEmptyPassword_ShouldReturnBadRequest()
  {
    // Arrange
    var enhancedLoginRequest = new EnhancedLoginRequest
    {
      Email = "test@example.com",
      Password = ""
    };

    // Act
    var result = await _controller.Login(enhancedLoginRequest);

    // Assert
    var badRequestResult = result.Should().BeOfType<BadRequestObjectResult>().Subject;
    var responseBody = badRequestResult.Value.Should().BeEquivalentTo(new { message = "Email and password are required" });
  }

  [Fact]
  public async Task Register_WithWeakPassword_ReturnsSpecificPasswordErrorWithoutCallingService()
  {
    var request = new RegisterRequest
    {
      Email = "new@example.com",
      Password = "Mypass99", // valid except: no special character
      FirstName = "New",
      LastName = "User"
    };

    var result = await _controller.Register(request);

    var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
    var message = bad.Value!.GetType().GetProperty("message")!.GetValue(bad.Value) as string;
    message.Should().Contain("special character");
    // Validation happens before the service is touched.
    _mockAuthService.Verify(s => s.RegisterAsync(It.IsAny<RegisterRequest>()), Times.Never);
  }

  [Fact]
  public async Task Register_WhenServiceReturnsNull_ReturnsEmailAlreadyRegistered()
  {
    var request = new RegisterRequest
    {
      Email = "taken@example.com",
      Password = "Str0ng!Pass9", // passes validation
      FirstName = "Taken",
      LastName = "User"
    };
    _mockAuthService.Setup(s => s.RegisterAsync(It.IsAny<RegisterRequest>())).ReturnsAsync((LoginResponse?)null);

    var result = await _controller.Register(request);

    var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
    var message = bad.Value!.GetType().GetProperty("message")!.GetValue(bad.Value) as string;
    message.Should().Contain("already registered");
  }

  [Fact]
  public async Task VerifyPassword_CorrectPassword_ReturnsOkValidTrue()
  {
    SetAuthenticatedUser(7);
    _mockAuthService.Setup(x => x.VerifyPasswordAsync(7, "correct-horse")).ReturnsAsync(true);

    var result = await _controller.VerifyPassword(new VerifyPasswordRequest { Password = "correct-horse" });

    var ok = result.Should().BeOfType<OkObjectResult>().Subject;
    var body = ok.Value.Should().BeOfType<VerifyPasswordResponse>().Subject;
    body.Valid.Should().BeTrue();
    // Must not mint tokens / issue a refresh token for a mere password check.
    _mockRefreshTokenService.Verify(x => x.IssueAsync(It.IsAny<int>(), It.IsAny<Guid?>()), Times.Never);
    _mockAuthService.Verify(x => x.LoginAsync(It.IsAny<LoginRequest>()), Times.Never);
  }

  [Fact]
  public async Task VerifyPassword_WrongPassword_ReturnsOkValidFalse()
  {
    SetAuthenticatedUser(7);
    _mockAuthService.Setup(x => x.VerifyPasswordAsync(7, "nope")).ReturnsAsync(false);

    var result = await _controller.VerifyPassword(new VerifyPasswordRequest { Password = "nope" });

    var ok = result.Should().BeOfType<OkObjectResult>().Subject;
    var body = ok.Value.Should().BeOfType<VerifyPasswordResponse>().Subject;
    body.Valid.Should().BeFalse();
  }

  [Fact]
  public async Task VerifyPassword_MissingPassword_ReturnsBadRequest()
  {
    SetAuthenticatedUser(7);

    var result = await _controller.VerifyPassword(new VerifyPasswordRequest { Password = "" });

    result.Should().BeOfType<BadRequestObjectResult>();
    _mockAuthService.Verify(x => x.VerifyPasswordAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
  }

  [Fact]
  public async Task Login_WhenServiceThrowsException_ShouldPropagateException()
  {
    // Arrange
    var enhancedLoginRequest = new EnhancedLoginRequest
    {
      Email = "test@example.com",
      Password = "password123"
    };

    _mockAuthService.Setup(x => x.ValidateCredentialsAsync(It.IsAny<LoginRequest>()))
      .ThrowsAsync(new Exception("Database connection failed"));

    // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
    await Assert.ThrowsAsync<Exception>(() => _controller.Login(enhancedLoginRequest));
  }

  // ---- Refresh cookie: Secure only over HTTPS, own name on a self-hosted server ----

  private string RefreshWithCookie(bool https, bool selfHost)
  {
    var http = _controller.ControllerContext.HttpContext;
    http.Request.Scheme = https ? "https" : "http";
    if (selfHost)
    {
      var services = new Mock<IServiceProvider>();
      services.Setup(x => x.GetService(typeof(BuoyAI.Infrastructure.Configuration.EditionInfo)))
        .Returns(new BuoyAI.Infrastructure.Configuration.EditionInfo(BuoyAI.Infrastructure.Configuration.Edition.SelfHost));
      http.RequestServices = services.Object;
    }
    _mockAuthService.Setup(x => x.RefreshTokenAsync(It.IsAny<string>()))
      .ReturnsAsync(new LoginResponse { Token = "t", RefreshToken = "new-refresh" });

    var result = _controller.Refresh(new RefreshTokenRequest { RefreshToken = "old" }).GetAwaiter().GetResult();

    result.Should().BeOfType<OkObjectResult>();
    return http.Response.Headers.SetCookie.ToString();
  }

  [Fact]
  public void RefreshCookie_OverHttps_IsSecureHttpOnlyLax()
  {
    var cookie = RefreshWithCookie(https: true, selfHost: false);
    cookie.Should().StartWith("refresh_token=new-refresh");
    cookie.Should().Contain("secure").And.Contain("httponly").And.Contain("samesite=lax");
  }

  [Fact]
  public void RefreshCookie_OverPlainHttp_IsNotSecure_SoALanInstallKeepsItsSession()
  {
    var cookie = RefreshWithCookie(https: false, selfHost: false);
    cookie.Should().StartWith("refresh_token=new-refresh");
    cookie.Should().NotContain("secure");
    cookie.Should().Contain("httponly").And.Contain("samesite=lax");
  }

  [Fact]
  public void RefreshCookie_OnASelfHostedServer_HasItsOwnName()
  {
    var cookie = RefreshWithCookie(https: false, selfHost: true);
    cookie.Should().StartWith("buoy_money_refresh=new-refresh");
    cookie.Should().NotContain("secure");
  }
}
