using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.Threading;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.DTOs.Plaid;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Utilities;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Telemetry;
using System.Diagnostics.Metrics;

namespace BuoyAI.UnitTests.Services;

public class AuthServiceTests : IDisposable
{
  private readonly Mock<IUserRepository> _mockUserRepository;
  private readonly Mock<IConfiguration> _mockConfiguration;
  private readonly Mock<IBankAggregator> _mockAggregator;
  private readonly Mock<ILastSyncTracker> _mockLastSyncTracker;
  private readonly Mock<ILogger<AuthService>> _mockLogger;
  private readonly Mock<IEmailService> _mockEmailService;
  private readonly Mock<IEmailPreferencesService> _mockEmailPreferencesService;
  private readonly Mock<IHostEnvironment> _mockEnvironment;
  private readonly Mock<IBillingService> _mockBillingService;
  private readonly Mock<INotificationService> _mockNotificationService;
  private readonly Mock<IDataRetentionManager> _mockDataRetentionManager;
  private readonly Mock<IFamilyInvitationRepository> _mockFamilyInvitationRepository;
  private readonly Mock<IGoogleTokenValidator> _mockGoogleTokenValidator;
  private readonly Mock<IAppleTokenValidator> _mockAppleTokenValidator;
  private readonly Mock<IRefreshTokenService> _mockRefreshTokenService;
  private readonly Mock<IEncryptionService> _mockEncryptionService;
  private readonly Mock<IAppleAuthClient> _mockAppleAuthClient;
  private readonly Mock<IMarketingLeadService> _mockMarketingLeadService;
  private readonly Mock<ITrialProvisioningService> _mockTrialProvisioning;
  private readonly BuoyAIDbContext _context;
  private readonly BuoyAiMetrics _metrics;
  private readonly AuthService _authService;

  private const string TestJwtKey = "your-super-secret-jwt-key-that-should-be-at-least-32-characters-long";
  private const string TestJwtIssuer = "BuoyAI";
  private const string TestJwtAudience = "BuoyAI-Users";

  public AuthServiceTests()
  {
    _mockUserRepository = new Mock<IUserRepository>();
    _mockConfiguration = new Mock<IConfiguration>();
    _mockAggregator = new Mock<IBankAggregator>();
    _mockAggregator.SetupGet(a => a.Provider).Returns(BankProvider.Plaid);
    _mockLastSyncTracker = new Mock<ILastSyncTracker>();
    _mockLogger = new Mock<ILogger<AuthService>>();
    _mockEmailService = new Mock<IEmailService>();
    _mockEmailPreferencesService = new Mock<IEmailPreferencesService>();
    _mockEnvironment = new Mock<IHostEnvironment>();
    _mockBillingService = new Mock<IBillingService>();
    _mockNotificationService = new Mock<INotificationService>();
    _mockDataRetentionManager = new Mock<IDataRetentionManager>();
    _mockFamilyInvitationRepository = new Mock<IFamilyInvitationRepository>();
    _mockGoogleTokenValidator = new Mock<IGoogleTokenValidator>();
    _mockAppleTokenValidator = new Mock<IAppleTokenValidator>();
    _mockRefreshTokenService = new Mock<IRefreshTokenService>();
    _mockEncryptionService = new Mock<IEncryptionService>();
    _mockAppleAuthClient = new Mock<IAppleAuthClient>();
    // Default: no captured marketing lead, so registrations get the configured trial.
    // Tests that care about the extended offer override GetUnredeemedOfferDaysAsync.
    _mockMarketingLeadService = new Mock<IMarketingLeadService>();
    // Stripe provisioning is fire-and-forget from registration's point of view: it never
    // throws and its result does not change the local trial. Default to "succeeded".
    _mockTrialProvisioning = new Mock<ITrialProvisioningService>();
    _mockTrialProvisioning
      .Setup(x => x.ProvisionStripeTrialAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(true);
    _mockMarketingLeadService
      .Setup(x => x.GetUnredeemedOfferDaysAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync((int?)null);

    // Setup JWT configuration
    var jwtSection = new Mock<IConfigurationSection>();
    jwtSection.Setup(x => x["Key"]).Returns(TestJwtKey);
    jwtSection.Setup(x => x["Issuer"]).Returns(TestJwtIssuer);
    jwtSection.Setup(x => x["Audience"]).Returns(TestJwtAudience);

    _mockConfiguration.Setup(x => x.GetSection("Jwt")).Returns(jwtSection.Object);
    _mockConfiguration.Setup(x => x["Jwt:Key"]).Returns(TestJwtKey);
    _mockConfiguration.Setup(x => x["Jwt:Issuer"]).Returns(TestJwtIssuer);
    _mockConfiguration.Setup(x => x["Jwt:Audience"]).Returns(TestJwtAudience);
    _mockConfiguration.Setup(x => x["App:BaseUrl"]).Returns("http://localhost:3000");

    // Setup environment
    _mockEnvironment.Setup(x => x.EnvironmentName).Returns("Development");

    // Setup in-memory database
    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      // GoogleLoginAsync wraps work in a transaction; the in-memory provider
      // can't honor transactions, so ignore the warning instead of throwing.
      .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
      .Options;
    _context = new BuoyAIDbContext(options);

    // Create metrics using a test meter factory
    var meterFactory = new TestMeterFactory();
    _metrics = new BuoyAiMetrics(meterFactory);

    _authService = new AuthService(
      _mockUserRepository.Object,
      _mockConfiguration.Object,
      new Lazy<IBankAggregatorRegistry>(() => new BankAggregatorRegistry(new[] { _mockAggregator.Object })),
      _mockLastSyncTracker.Object,
      _mockLogger.Object,
      _context,
      _mockEmailService.Object,
      _mockEmailPreferencesService.Object,
      _mockEnvironment.Object,
      new ServiceCollection().AddSingleton(_mockBillingService.Object).AddSingleton(_mockTrialProvisioning.Object).BuildServiceProvider(),
      Mock.Of<IEntitlementService>(),
      _mockNotificationService.Object,
      _mockDataRetentionManager.Object,
      _mockFamilyInvitationRepository.Object,
      _mockGoogleTokenValidator.Object,
      _mockAppleTokenValidator.Object,
      _mockRefreshTokenService.Object,
      _metrics,
      _mockEncryptionService.Object,
      _mockAppleAuthClient.Object,
      Mock.Of<IServiceScopeFactory>(),
      _mockMarketingLeadService.Object);
  }

  public void Dispose()
  {
    _context.Database.EnsureDeleted();
    _context.Dispose();
    GC.SuppressFinalize(this);
  }

  [Fact]
  public async Task LoginAsync_WithValidCredentials_ShouldReturnLoginResponse()
  {
    // Arrange
    var username = "testuser";
    var email = "test@example.com";
    var password = "password123";
    var hashedPassword = AuthService.HashPassword(password);
    
    var user = new User
    {
      Id = 1,
      Username = username,
      PasswordHash = hashedPassword,
      Email = email,
      SubscriptionTier = SubscriptionTier.Standard
    };

    var loginRequest = new LoginRequest
    {
      Email = email,
      Password = password
    };

    _mockUserRepository.Setup(x => x.GetByEmailAsync(email))
      .ReturnsAsync(user);

    // Act
    var result = await _authService.LoginAsync(loginRequest);

    // Assert
    result.Should().NotBeNull();
  result!.User.Username.Should().Be(username);
  result.User.SubscriptionTier.Should().Be("Standard");
    result.Token.Should().NotBeNullOrEmpty();
    result.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
  }

  [Fact]
  public async Task LoginAsync_WithInvalidEmail_ShouldReturnNull()
  {
    // Arrange
    var loginRequest = new LoginRequest
    {
      Email = "nonexistent@example.com",
      Password = "password123"
    };

    _mockUserRepository.Setup(x => x.GetByEmailAsync("nonexistent@example.com"))
      .ReturnsAsync((User?)null);

    // Act
    var result = await _authService.LoginAsync(loginRequest);

    // Assert
    result.Should().BeNull();
  }

  [Fact]
  public async Task LoginAsync_WithInvalidPassword_ShouldReturnNull()
  {
    // Arrange
    var username = "testuser";
    var email = "test@example.com";
    var correctPassword = "password123";
    var wrongPassword = "wrongpassword";
    var hashedPassword = AuthService.HashPassword(correctPassword);

    var user = new User
    {
      Id = 1,
      Username = username,
      PasswordHash = hashedPassword,
      Email = email
    };

    var loginRequest = new LoginRequest
    {
      Email = email,
      Password = wrongPassword
    };

    _mockUserRepository.Setup(x => x.GetByEmailAsync(email))
      .ReturnsAsync(user);

    // Act
    var result = await _authService.LoginAsync(loginRequest);

    // Assert
    result.Should().BeNull();
  }

  [Fact]
  public async Task GetUserProfileAsync_WithValidUserId_ShouldReturnUserProfile()
  {
    // Arrange
    var userId = 1;
    var user = new User
    {
      Id = userId,
      Username = "testuser",
      Email = "test@example.com",
      FirstName = "Test",
      LastName = "User",
      Currency = "USD",
      SubscriptionTier = SubscriptionTier.Premium
    };

    _mockUserRepository.Setup(x => x.GetByIdAsync(userId))
      .ReturnsAsync(user);

    // Act
    var result = await _authService.GetUserProfileAsync(userId);

    // Assert
    result.Should().NotBeNull();
  result!.Id.Should().Be(userId);
  result.Username.Should().Be("testuser");
  result.Email.Should().Be("test@example.com");
  result.FirstName.Should().Be("Test");
  result.LastName.Should().Be("User");
  result.Currency.Should().Be("USD");
  result.SubscriptionTier.Should().Be("Premium");
  }

  [Fact]
  public async Task GetUserProfileAsync_WithInvalidUserId_ShouldReturnNull()
  {
    // Arrange
    var userId = 999;
    _mockUserRepository.Setup(x => x.GetByIdAsync(userId))
      .ReturnsAsync((User?)null);

    // Act
    var result = await _authService.GetUserProfileAsync(userId);

    // Assert
    result.Should().BeNull();
  }

  [Fact]
  public void GenerateJwtToken_ShouldReturnValidToken()
  {
    // Arrange
    var userId = 1;
    var username = "testuser";

    // Act
    var token = _authService.GenerateJwtToken(userId, username);

    // Assert
    token.Should().NotBeNullOrEmpty();
    token.Split('.').Should().HaveCount(3); // JWT should have 3 parts
  }

  [Fact]
  public void HashPassword_ShouldReturnHashedPassword()
  {
    // Arrange
    var password = "password123";

    // Act
    var hashedPassword = AuthService.HashPassword(password);

    // Assert
    hashedPassword.Should().NotBeNullOrEmpty();
    hashedPassword.Should().NotBe(password);
    BCrypt.Net.BCrypt.Verify(password, hashedPassword).Should().BeTrue();
  }

  #region RegisterAsync Tests

  [Fact]
  public async Task RegisterAsync_WithExistingEmail_ShouldReturnNull()
  {
    // Arrange
    var existingUser = new User { Id = 1, Email = "existing@example.com" };

    _mockUserRepository.Setup(x => x.GetByEmailAsync(existingUser.Email))
      .ReturnsAsync(existingUser);

    var request = new RegisterRequest
    {
      Email = existingUser.Email,
      Password = "SecurePassword123!",
      FirstName = "Test",
      LastName = "User"
    };

    // Act
    var result = await _authService.RegisterAsync(request);

    // Assert
    result.Should().BeNull();
    _mockUserRepository.Verify(x => x.CreateAsync(It.IsAny<User>()), Times.Never);
  }

  [Fact]
  public async Task RegisterAsync_WithWeakPassword_ShouldReturnNull()
  {
    // Arrange
    var request = new RegisterRequest
    {
      Email = "test@example.com",
      Password = "weak",
      FirstName = "Test",
      LastName = "User"
    };

    _mockUserRepository.Setup(x => x.GetByEmailAsync(request.Email))
      .ReturnsAsync((User?)null);

    // Act
    var result = await _authService.RegisterAsync(request);

    // Assert
    result.Should().BeNull();
    _mockUserRepository.Verify(x => x.CreateAsync(It.IsAny<User>()), Times.Never);
  }

  [Fact]
  public async Task RegisterAsync_WithInvalidEmail_ShouldReturnNull()
  {
    // Arrange
    var request = new RegisterRequest
    {
      Email = "invalid-email",
      Password = "SecurePassword123!",
      FirstName = "Test",
      LastName = "User"
    };

    // Act
    var result = await _authService.RegisterAsync(request);

    // Assert
    result.Should().BeNull();
    _mockUserRepository.Verify(x => x.CreateAsync(It.IsAny<User>()), Times.Never);
  }

  #endregion

  #region Password Reset Tests

  [Fact]
  public async Task RequestPasswordResetAsync_WithValidEmail_ShouldCreateTokenAndSendEmail()
  {
    // Arrange
    var email = "test@example.com";
    var user = new User { Id = 1, Email = email, FirstName = "Test" };

    _mockUserRepository.Setup(x => x.GetByEmailAsync(email))
      .ReturnsAsync(user);

    _mockEmailService.Setup(x => x.SendPasswordResetEmailAsync(email, It.IsAny<string>()))
      .Returns(Task.CompletedTask);

    // Act
    var result = await _authService.RequestPasswordResetAsync(email, "127.0.0.1", "TestAgent");

    // Assert
    result.Should().BeTrue();

    var resetToken = await _context.PasswordResetTokens.FirstOrDefaultAsync();
    resetToken.Should().NotBeNull();
    resetToken!.UserId.Should().Be(user.Id);

    _mockEmailService.Verify(x => x.SendPasswordResetEmailAsync(email, It.IsAny<string>()), Times.Once);
  }

  [Fact]
  public async Task RequestPasswordResetAsync_WithNonExistentEmail_ShouldReturnTrueToPreventEnumeration()
  {
    // Arrange
    _mockUserRepository.Setup(x => x.GetByEmailAsync(It.IsAny<string>()))
      .ReturnsAsync((User?)null);

    // Act
    var result = await _authService.RequestPasswordResetAsync("nonexistent@example.com");

    // Assert
    result.Should().BeTrue();
    
    var resetToken = await _context.PasswordResetTokens.FirstOrDefaultAsync();
    resetToken.Should().BeNull();

    _mockEmailService.Verify(x => x.SendPasswordResetEmailAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
  }

  [Fact]
  public async Task ValidatePasswordResetTokenAsync_WithValidToken_ShouldReturnTrue()
  {
    // Arrange
    var token = "valid-token-123";
    var user = new User { Id = 1, Email = "test@example.com" };

    var resetToken = new PasswordResetToken
    {
      UserId = user.Id,
      User = user,
      Token = token,
      ExpiresAt = DateTime.UtcNow.AddHours(1),
      CreatedAt = DateTime.UtcNow
    };

    _context.PasswordResetTokens.Add(resetToken);
    await _context.SaveChangesAsync();

    // Act
    var result = await _authService.ValidatePasswordResetTokenAsync(token);

    // Assert
    result.Should().BeTrue();
  }

  #endregion

  #region MFA Token Tests

  [Fact]
  public async Task GenerateMfaTokenAsync_ShouldReturnValidShortLivedToken()
  {
    // Arrange
    var userId = 1;

    // Act
    var token = await _authService.GenerateMfaTokenAsync(userId);

    // Assert
    token.Should().NotBeNullOrEmpty();

    var tokenHandler = new JwtSecurityTokenHandler();
    var key = Encoding.ASCII.GetBytes(TestJwtKey);

    var validationParameters = new TokenValidationParameters
    {
      ValidateIssuerSigningKey = true,
      IssuerSigningKey = new SymmetricSecurityKey(key),
      ValidateIssuer = true,
      ValidIssuer = TestJwtIssuer,
      ValidateAudience = true,
      ValidAudience = TestJwtAudience,
      ValidateLifetime = true,
      ClockSkew = TimeSpan.Zero
    };

    var principal = tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);

    principal.FindFirst(ClaimTypes.NameIdentifier)?.Value.Should().Be(userId.ToString());
    principal.FindFirst("type")?.Value.Should().Be("mfa_pending");
    validatedToken.ValidTo.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(10));
  }

  [Fact]
  public async Task CompleteMfaLoginAsync_WithValidTokenAndCode_ShouldReturnLoginResponse()
  {
    // Arrange
    var userId = 1;
    var mfaCode = "654321"; // Use different code to avoid dev fallback

    var user = new User
    {
      Id = userId,
      Email = "test@example.com",
      Username = "testuser",
      FirstName = "Test",
      LastName = "User",
      PasswordHash = "hash",
      Currency = "USD"
    };

    var twoFactorCode = new TwoFactorCode
    {
      UserId = userId,
      Code = mfaCode,
      ExpiresAt = DateTime.UtcNow.AddMinutes(10),
      IsUsed = false,
      Purpose = "login_verification",
      CreatedAt = DateTime.UtcNow
    };

    _context.TwoFactorCodes.Add(twoFactorCode);
    await _context.SaveChangesAsync();

    _mockUserRepository.Setup(x => x.GetByIdAsync(userId))
      .ReturnsAsync(user);

    _mockLastSyncTracker.Setup(x => x.IsDataStale(userId, It.IsAny<TimeSpan>()))
      .ReturnsAsync(false);

    var mfaToken = await _authService.GenerateMfaTokenAsync(userId);

    // Act
    var result = await _authService.CompleteMfaLoginAsync(mfaToken, mfaCode);

    // Assert
    result.Should().NotBeNull();
    result!.Token.Should().NotBeNullOrEmpty();

    var usedCode = await _context.TwoFactorCodes.FirstOrDefaultAsync();
    usedCode!.IsUsed.Should().BeTrue();
  }

  [Fact]
  public async Task ValidateMfaToken_GenuineToken_ReturnsUserId()
  {
    var token = await _authService.GenerateMfaTokenAsync(1234);

    _authService.ValidateMfaToken(token).Should().Be(1234);
  }

  [Fact]
  public void ValidateMfaToken_ForgedUnsignedToken_ReturnsNull()
  {
    // An attacker hand-crafts an unsigned JWT claiming userId=999, type=mfa_pending.
    // The old send-login-mfa-code path only *decoded* the token, so this would drive an
    // email send / enumeration probe. Full validation must reject it (bad signature).
    var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
    var forged = handler.WriteToken(new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
      claims: new[]
      {
        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "999"),
        new System.Security.Claims.Claim("type", "mfa_pending")
      }));

    _authService.ValidateMfaToken(forged).Should().BeNull();
  }

  [Fact]
  public void ValidateMfaToken_WrongKeySignedToken_ReturnsNull()
  {
    // Signed with a different key than ours → must fail signature validation.
    var otherKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
      System.Text.Encoding.ASCII.GetBytes("a-completely-different-signing-key-value-1234567890"));
    var creds = new Microsoft.IdentityModel.Tokens.SigningCredentials(
      otherKey, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256Signature);
    var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
    var token = handler.WriteToken(new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
      issuer: "test-issuer",
      audience: "test-audience",
      claims: new[]
      {
        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "5"),
        new System.Security.Claims.Claim("type", "mfa_pending")
      },
      expires: DateTime.UtcNow.AddMinutes(5),
      signingCredentials: creds));

    _authService.ValidateMfaToken(token).Should().BeNull();
  }

  #endregion

  #region GoogleLoginAsync intent

  private GoogleTokenPayload BuildGooglePayload(
    string email = "newperson@example.com", string subject = "google-subject-123", bool emailVerified = true)
    => new()
    {
      Subject = subject,
      Email = email,
      EmailVerified = emailVerified,
      GivenName = "New",
      FamilyName = "Person",
      Picture = "https://example.com/pic.png"
    };

  [Fact]
  public async Task GoogleLoginAsync_UnverifiedEmail_MatchingExistingPasswordAccount_IsRejected_NoTakeover()
  {
    // Account-takeover guard: an attacker's Google token carries the victim's email but
    // email_verified=false. The login must be rejected and must NOT link a GoogleId onto
    // the victim's pre-existing password account or return a session for it.
    var victim = new User
    {
      Id = 42,
      Email = "victim@customdomain.com",
      Username = "victim",
      PasswordHash = "victim-bcrypt-hash",
      Currency = "USD"
    };
    var payload = BuildGooglePayload(email: victim.Email, subject: "attacker-google-sub", emailVerified: false);
    _mockGoogleTokenValidator.Setup(v => v.ValidateAsync(It.IsAny<string>())).ReturnsAsync(payload);
    _mockUserRepository.Setup(r => r.GetByGoogleIdAsync(payload.Subject)).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.GetByEmailAsync(victim.Email)).ReturnsAsync(victim);

    var result = await _authService.GoogleLoginAsync(
      new GoogleLoginRequest { IdToken = "token", IsRegistration = false });

    result.Should().BeNull();
    // The victim's account was never touched (no GoogleId linked, no update persisted).
    _mockUserRepository.Verify(r => r.UpdateAsync(It.IsAny<User>()), Times.Never);
    victim.GoogleId.Should().BeNull();
  }

  [Fact]
  public async Task GoogleLoginAsync_ThrowawayDomain_ThrowsBlockedDomain_NotABareNull()
  {
    // A refused throwaway domain used to return null, which the controller mapped to 401
    // "Invalid Google token" — the same answer a broken token audience gives. That made a
    // working-as-designed rejection indistinguishable from an outage in metrics.
    var payload = BuildGooglePayload(email: "throwaway@mailinator.com");
    _mockGoogleTokenValidator.Setup(v => v.ValidateAsync(It.IsAny<string>())).ReturnsAsync(payload);
    _mockUserRepository.Setup(r => r.GetByGoogleIdAsync(payload.Subject)).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.GetByEmailAsync(payload.Email)).ReturnsAsync((User?)null);

    var act = async () => await _authService.GoogleLoginAsync(
      new GoogleLoginRequest { IdToken = "token", IsRegistration = true });

    var thrown = await act.Should().ThrowAsync<GoogleRegistrationRejectedException>();
    thrown.Which.Reason.Should().Be(GoogleRegistrationRejection.BlockedDomain);
    _mockUserRepository.Verify(r => r.CreateAsync(It.IsAny<User>()), Times.Never);
  }

  [Fact]
  public async Task GoogleLoginAsync_NoAccount_StillThrowsAccountNotFound_SoThe404ContractHolds()
  {
    // Guards the one status the Flutter client keys on. Installed apps redirect to
    // register on 404; moving it would drop them into a generic error toast instead,
    // which is the exact failure the register-landing fix exists to remove.
    var payload = BuildGooglePayload(email: "nobody@gmail.com");
    _mockGoogleTokenValidator.Setup(v => v.ValidateAsync(It.IsAny<string>())).ReturnsAsync(payload);
    _mockUserRepository.Setup(r => r.GetByGoogleIdAsync(payload.Subject)).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.GetByEmailAsync(payload.Email)).ReturnsAsync((User?)null);

    var act = async () => await _authService.GoogleLoginAsync(
      new GoogleLoginRequest { IdToken = "token", IsRegistration = false });

    await act.Should().ThrowAsync<GoogleAccountNotFoundException>();
  }

  [Fact]
  public async Task GoogleLoginAsync_LoginIntent_WithNoExistingAccount_ShouldThrowGoogleAccountNotFound()
  {
    // Arrange — login flow (IsRegistration = false) with no matching account.
    var payload = BuildGooglePayload();
    _mockGoogleTokenValidator.Setup(v => v.ValidateAsync(It.IsAny<string>())).ReturnsAsync(payload);
    _mockUserRepository.Setup(r => r.GetByGoogleIdAsync(payload.Subject)).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.GetByEmailAsync(payload.Email)).ReturnsAsync((User?)null);

    var request = new GoogleLoginRequest { IdToken = "token", IsRegistration = false };

    // Act
    var act = async () => await _authService.GoogleLoginAsync(request);

    // Assert — must signal "no account" and never create a user.
    await act.Should().ThrowAsync<GoogleAccountNotFoundException>();
    _mockUserRepository.Verify(r => r.CreateAsync(It.IsAny<User>()), Times.Never);
  }

  [Fact]
  public async Task GoogleLoginAsync_RegistrationIntent_WithNoExistingAccount_ShouldCreateUserAndReturnResponse()
  {
    // Arrange — registration flow (IsRegistration = true) with no matching account.
    var trialSection = new Mock<IConfigurationSection>();
    trialSection.Setup(s => s.Value).Returns("14");
    _mockConfiguration.Setup(c => c.GetSection("Subscription:TrialPeriodDays")).Returns(trialSection.Object);

    var payload = BuildGooglePayload();
    _mockGoogleTokenValidator.Setup(v => v.ValidateAsync(It.IsAny<string>())).ReturnsAsync(payload);
    _mockUserRepository.Setup(r => r.GetByGoogleIdAsync(payload.Subject)).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.GetByEmailAsync(payload.Email)).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.GetByUsernameAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.CountGoogleRegistrationsSinceAsync(It.IsAny<DateTime>())).ReturnsAsync(0);
    _mockUserRepository
      .Setup(r => r.CreateAsync(It.IsAny<User>()))
      .ReturnsAsync((User u) => { u.Id = 4321; return u; });

    var request = new GoogleLoginRequest { IdToken = "token", IsRegistration = true };

    // Act
    var response = await _authService.GoogleLoginAsync(request);

    // Assert — new account created with a trial and a usable session token.
    response.Should().NotBeNull();
    response!.Token.Should().NotBeNullOrEmpty();
    _mockUserRepository.Verify(r => r.CreateAsync(It.Is<User>(u =>
      u.IsGoogleUser && u.IsTrialActive && u.Email == payload.Email)), Times.Once);
  }

  [Fact]
  public async Task GoogleLoginAsync_RegistrationIntent_FirstTimeEmail_GrantsTrialAndRecordsHistory()
  {
    var trialSection = new Mock<IConfigurationSection>();
    trialSection.Setup(s => s.Value).Returns("14");
    _mockConfiguration.Setup(c => c.GetSection("Subscription:TrialPeriodDays")).Returns(trialSection.Object);

    var payload = BuildGooglePayload(email: "fresh@example.com", subject: "google-fresh-1");
    _mockGoogleTokenValidator.Setup(v => v.ValidateAsync(It.IsAny<string>())).ReturnsAsync(payload);
    _mockUserRepository.Setup(r => r.GetByGoogleIdAsync(payload.Subject)).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.GetByEmailAsync(payload.Email)).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.GetByUsernameAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.CountGoogleRegistrationsSinceAsync(It.IsAny<DateTime>())).ReturnsAsync(0);
    _mockUserRepository.Setup(r => r.CreateAsync(It.IsAny<User>()))
      .ReturnsAsync((User u) => { u.Id = 555; return u; });

    var response = await _authService.GoogleLoginAsync(
      new GoogleLoginRequest { IdToken = "token", IsRegistration = true });

    response.Should().NotBeNull();
    // Google accounts are created already email-verified (provider vetted the email).
    _mockUserRepository.Verify(r => r.CreateAsync(It.Is<User>(u =>
      u.IsTrialActive && u.HasUsedTrial && u.TrialEndDate != null
      && !u.RequiresPaidPlan && u.IsEmailVerified)), Times.Once);

    var expectedHash = TrialEmailHasher.Compute(payload.Email);
    var history = _context.TrialHistories.SingleOrDefault(t => t.EmailHash == expectedHash);
    history.Should().NotBeNull();
    history!.FirstSource.Should().Be("google");
    history.FirstUserId.Should().Be(555);

    // One day-0 email: the trial welcome (whose button is "Connect my bank"), not also the
    // plain welcome. Sending both used to contradict itself about whether a card was needed.
    _mockEmailService.Verify(e => e.SendTrialInvitationAsync(payload.Email, It.IsAny<string>(), 14), Times.Once);
    _mockEmailService.Verify(e => e.SendWelcomeEmailAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
  }

  [Fact]
  public async Task GoogleLoginAsync_RegistrationIntent_EmailWithPriorTrial_CreatesAccountWithoutTrial()
  {
    var trialSection = new Mock<IConfigurationSection>();
    trialSection.Setup(s => s.Value).Returns("14");
    _mockConfiguration.Setup(c => c.GetSection("Subscription:TrialPeriodDays")).Returns(trialSection.Object);

    var payload = BuildGooglePayload(email: "returning@example.com", subject: "google-returning-1");

    // Simulate a prior trial on this email (e.g. on a since-deleted account).
    _context.TrialHistories.Add(new TrialHistory
    {
      EmailHash = TrialEmailHasher.Compute(payload.Email)!,
      FirstTrialStartedAt = DateTime.UtcNow.AddMonths(-3),
      LastTrialGrantedAt = DateTime.UtcNow.AddMonths(-3),
      TrialGrantCount = 1,
      FirstSource = "email",
      CreatedAt = DateTime.UtcNow.AddMonths(-3),
      UpdatedAt = DateTime.UtcNow.AddMonths(-3),
    });
    _context.SaveChanges();

    _mockGoogleTokenValidator.Setup(v => v.ValidateAsync(It.IsAny<string>())).ReturnsAsync(payload);
    _mockUserRepository.Setup(r => r.GetByGoogleIdAsync(payload.Subject)).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.GetByEmailAsync(payload.Email)).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.GetByUsernameAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.CountGoogleRegistrationsSinceAsync(It.IsAny<DateTime>())).ReturnsAsync(0);
    _mockUserRepository.Setup(r => r.CreateAsync(It.IsAny<User>()))
      .ReturnsAsync((User u) => { u.Id = 556; return u; });

    var response = await _authService.GoogleLoginAsync(
      new GoogleLoginRequest { IdToken = "token", IsRegistration = true });

    // Account is still created, but with no active trial and the paywall flag set.
    response.Should().NotBeNull();
    _mockUserRepository.Verify(r => r.CreateAsync(It.Is<User>(u =>
      !u.IsTrialActive && u.HasUsedTrial && u.TrialStartDate == null
      && u.TrialEndDate == null && u.RequiresPaidPlan)), Times.Once);

    // No duplicate history row was added.
    _context.TrialHistories.Count(t => t.EmailHash == TrialEmailHasher.Compute(payload.Email)).Should().Be(1);

    // No trial, so the plain welcome is the one day-0 email.
    _mockEmailService.Verify(e => e.SendWelcomeEmailAsync(payload.Email, It.IsAny<string>()), Times.Once);
    _mockEmailService.Verify(e => e.SendTrialInvitationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
  }

  [Fact]
  public async Task GoogleLoginAsync_CapturedLead_GrantsTheExtendedTrial()
  {
    var trialSection = new Mock<IConfigurationSection>();
    trialSection.Setup(s => s.Value).Returns("14");
    _mockConfiguration.Setup(c => c.GetSection("Subscription:TrialPeriodDays")).Returns(trialSection.Object);

    var payload = BuildGooglePayload(email: "lead@example.com", subject: "google-lead-1");

    // The marketing popup captured this address and offered the one-month trial.
    _mockMarketingLeadService
      .Setup(x => x.GetUnredeemedOfferDaysAsync(payload.Email, It.IsAny<CancellationToken>()))
      .ReturnsAsync(30);

    _mockGoogleTokenValidator.Setup(v => v.ValidateAsync(It.IsAny<string>())).ReturnsAsync(payload);
    _mockUserRepository.Setup(r => r.GetByGoogleIdAsync(payload.Subject)).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.GetByEmailAsync(payload.Email)).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.GetByUsernameAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.CountGoogleRegistrationsSinceAsync(It.IsAny<DateTime>())).ReturnsAsync(0);
    _mockUserRepository.Setup(r => r.CreateAsync(It.IsAny<User>()))
      .ReturnsAsync((User u) => { u.Id = 601; return u; });

    var response = await _authService.GoogleLoginAsync(
      new GoogleLoginRequest { IdToken = "token", IsRegistration = true });

    response.Should().NotBeNull();
    _mockUserRepository.Verify(r => r.CreateAsync(It.Is<User>(u =>
      u.IsTrialActive && u.TrialEndDate != null && u.TrialStartDate != null
      && (u.TrialEndDate.Value - u.TrialStartDate.Value).Days == 30)), Times.Once);

    // The offer is consumed so it can never be spent twice.
    _mockMarketingLeadService.Verify(
      x => x.MarkRedeemedAsync(payload.Email, 601, It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task GoogleLoginAsync_CapturedLead_CannotResurrectAnAlreadyUsedTrial()
  {
    var trialSection = new Mock<IConfigurationSection>();
    trialSection.Setup(s => s.Value).Returns("14");
    _mockConfiguration.Setup(c => c.GetSection("Subscription:TrialPeriodDays")).Returns(trialSection.Object);

    var payload = BuildGooglePayload(email: "abuser@example.com", subject: "google-abuser-1");

    // This address already burned its lifetime trial three months ago...
    _context.TrialHistories.Add(new TrialHistory
    {
      EmailHash = TrialEmailHasher.Compute(payload.Email)!,
      FirstTrialStartedAt = DateTime.UtcNow.AddMonths(-3),
      LastTrialGrantedAt = DateTime.UtcNow.AddMonths(-3),
      OriginalTrialEndsAt = DateTime.UtcNow.AddMonths(-2),
      TrialGrantCount = 1,
      FirstSource = "email",
      CreatedAt = DateTime.UtcNow.AddMonths(-3),
      UpdatedAt = DateTime.UtcNow.AddMonths(-3),
    });
    _context.SaveChanges();

    // ...and has now filled in the marketing popup hoping for a fresh extended trial.
    _mockMarketingLeadService
      .Setup(x => x.GetUnredeemedOfferDaysAsync(payload.Email, It.IsAny<CancellationToken>()))
      .ReturnsAsync(30);

    _mockGoogleTokenValidator.Setup(v => v.ValidateAsync(It.IsAny<string>())).ReturnsAsync(payload);
    _mockUserRepository.Setup(r => r.GetByGoogleIdAsync(payload.Subject)).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.GetByEmailAsync(payload.Email)).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.GetByUsernameAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.CountGoogleRegistrationsSinceAsync(It.IsAny<DateTime>())).ReturnsAsync(0);
    _mockUserRepository.Setup(r => r.CreateAsync(It.IsAny<User>()))
      .ReturnsAsync((User u) => { u.Id = 602; return u; });

    await _authService.GoogleLoginAsync(
      new GoogleLoginRequest { IdToken = "token", IsRegistration = true });

    // The paywall still wins: the popup changes the REQUESTED length, never the policy.
    _mockUserRepository.Verify(r => r.CreateAsync(It.Is<User>(u =>
      !u.IsTrialActive && u.TrialEndDate == null && u.RequiresPaidPlan)), Times.Once);

    // Nothing was granted, so the offer stays unspent rather than being silently burned.
    _mockMarketingLeadService.Verify(
      x => x.MarkRedeemedAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
      Times.Never);
  }

  [Fact]
  public async Task GoogleLoginAsync_RegistrationIntent_EmailWithUnexpiredTrial_ResumesRemainingDays()
  {
    var trialSection = new Mock<IConfigurationSection>();
    trialSection.Setup(s => s.Value).Returns("14");
    _mockConfiguration.Setup(c => c.GetSection("Subscription:TrialPeriodDays")).Returns(trialSection.Object);

    var payload = BuildGooglePayload(email: "resumer@example.com", subject: "google-resume-1");

    // Prior trial whose ORIGINAL window still has time left (deleted mid-trial).
    var originalEnd = DateTime.UtcNow.AddDays(12);
    _context.TrialHistories.Add(new TrialHistory
    {
      EmailHash = TrialEmailHasher.Compute(payload.Email)!,
      FirstTrialStartedAt = DateTime.UtcNow.AddDays(-18),
      OriginalTrialEndsAt = originalEnd,
      LastTrialGrantedAt = DateTime.UtcNow.AddDays(-18),
      TrialGrantCount = 1,
      FirstSource = "email",
      CreatedAt = DateTime.UtcNow.AddDays(-18),
      UpdatedAt = DateTime.UtcNow.AddDays(-18),
    });
    _context.SaveChanges();

    _mockGoogleTokenValidator.Setup(v => v.ValidateAsync(It.IsAny<string>())).ReturnsAsync(payload);
    _mockUserRepository.Setup(r => r.GetByGoogleIdAsync(payload.Subject)).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.GetByEmailAsync(payload.Email)).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.GetByUsernameAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.CountGoogleRegistrationsSinceAsync(It.IsAny<DateTime>())).ReturnsAsync(0);
    _mockUserRepository.Setup(r => r.CreateAsync(It.IsAny<User>()))
      .ReturnsAsync((User u) => { u.Id = 557; return u; });

    var response = await _authService.GoogleLoginAsync(
      new GoogleLoginRequest { IdToken = "token", IsRegistration = true });

    response.Should().NotBeNull();
    // Trial resumes to the original end date; no paywall.
    _mockUserRepository.Verify(r => r.CreateAsync(It.Is<User>(u =>
      u.IsTrialActive && u.HasUsedTrial && !u.RequiresPaidPlan
      && u.TrialEndDate == originalEnd)), Times.Once);
  }

  [Fact]
  public async Task GoogleLoginAsync_LoginIntent_WithExistingGoogleUser_ShouldLogInWithoutCreating()
  {
    // Arrange — existing account resolved by Google ID; login must succeed.
    var payload = BuildGooglePayload(email: "existing@example.com", subject: "google-existing-9");
    var existingUser = new User
    {
      Id = 88,
      Email = payload.Email,
      Username = "existing-user",
      GoogleId = payload.Subject,
      IsGoogleUser = true,
      SubscriptionTier = SubscriptionTier.Premium
    };
    _mockGoogleTokenValidator.Setup(v => v.ValidateAsync(It.IsAny<string>())).ReturnsAsync(payload);
    _mockUserRepository.Setup(r => r.GetByGoogleIdAsync(payload.Subject)).ReturnsAsync(existingUser);
    _mockUserRepository.Setup(r => r.UpdateAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);

    var request = new GoogleLoginRequest { IdToken = "token", IsRegistration = false };

    // Act
    var response = await _authService.GoogleLoginAsync(request);

    // Assert — logs the existing user in; does not create a new account.
    response.Should().NotBeNull();
    response!.Token.Should().NotBeNullOrEmpty();
    response.User.Email.Should().Be(payload.Email);
    _mockUserRepository.Verify(r => r.CreateAsync(It.IsAny<User>()), Times.Never);
  }

  #endregion

  #region AppleLoginAsync intent

  [Fact]
  public async Task AppleLoginAsync_UnverifiedEmail_CollidesWithExistingAccount_RejectsCleanly()
  {
    // Account-linking-by-unverified-email guard + collision handling: a token whose email
    // collides with an existing (non-deleted) account but with email_verified=false must NOT
    // resolve to / link onto that account. Because the email lookup is skipped, the code would
    // otherwise fall into the create block and attempt an insert that collides with the UNIQUE
    // email index -> DbUpdateException -> HTTP 500. Instead we must fail closed cleanly: reject
    // the login (null), attempt no create, and leave the existing account untouched.
    var victim = new User
    {
      Id = 77,
      Email = "victim@customdomain.com",
      Username = "victim",
      PasswordHash = "victim-hash",
      Currency = "USD"
    };
    _mockAppleTokenValidator
      .Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<string?>()))
      .ReturnsAsync(new AppleTokenPayload
      {
        Subject = "apple-attacker-sub",
        Email = victim.Email,
        EmailVerified = false
      });
    var trialSection = new Mock<IConfigurationSection>();
    trialSection.Setup(s => s.Value).Returns("0");
    _mockConfiguration.Setup(c => c.GetSection("Subscription:TrialPeriodDays")).Returns(trialSection.Object);
    _mockUserRepository.Setup(r => r.GetByAppleIdAsync("apple-attacker-sub")).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.GetByEmailAsync(It.IsAny<string>())).ReturnsAsync(victim);
    _mockUserRepository.Setup(r => r.GetByUsernameAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.CreateAsync(It.IsAny<User>()))
      .ReturnsAsync((User u) => { u.Id = 900; return u; });

    var response = await _authService.AppleLoginAsync(new AppleLoginRequest
    {
      IdentityToken = "token",
      Nonce = "nonce-123"
    });

    // Fail closed: no session, no create attempted, and the existing account is untouched
    // (neither linked nor stamped with the attacker's Apple ID).
    response.Should().BeNull();
    _mockUserRepository.Verify(r => r.CreateAsync(It.IsAny<User>()), Times.Never);
    _mockUserRepository.Verify(r => r.UpdateAsync(It.Is<User>(u => u.Id == victim.Id)), Times.Never);
    victim.AppleId.Should().BeNull();
  }

  [Fact]
  public async Task AppleLoginAsync_UnverifiedEmail_NoCollision_CreatesFreshAccount()
  {
    // The genuinely-new unverified-email case is unaffected by the collision guard: when no
    // existing account owns the email, a brand-new account keyed on the verified Apple `sub`
    // is created (and marked email-unverified since Apple didn't verify it).
    _mockAppleTokenValidator
      .Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<string?>()))
      .ReturnsAsync(new AppleTokenPayload
      {
        Subject = "apple-newuser-sub",
        Email = "brand-new@customdomain.com",
        EmailVerified = false
      });
    var trialSection = new Mock<IConfigurationSection>();
    trialSection.Setup(s => s.Value).Returns("0");
    _mockConfiguration.Setup(c => c.GetSection("Subscription:TrialPeriodDays")).Returns(trialSection.Object);
    _mockUserRepository.Setup(r => r.GetByAppleIdAsync("apple-newuser-sub")).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.GetByEmailAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.GetByUsernameAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.CreateAsync(It.IsAny<User>()))
      .ReturnsAsync((User u) => { u.Id = 901; return u; });

    var response = await _authService.AppleLoginAsync(new AppleLoginRequest
    {
      IdentityToken = "token",
      Nonce = "nonce-123"
    });

    // A fresh account keyed on the Apple sub is created; no existing account to link.
    _mockUserRepository.Verify(r => r.CreateAsync(It.Is<User>(u =>
      u.AppleId == "apple-newuser-sub" && !u.IsEmailVerified)), Times.Once);
    response.Should().NotBeNull();
    response!.User.Id.Should().Be(901);
  }

  [Fact]
  public async Task AppleLoginAsync_VerifiedEmail_LinksOntoExistingAccount()
  {
    // The legitimate path: Apple-verified email that matches an existing account links the
    // Apple identity onto it (no new account) and logs that user in.
    var existing = new User
    {
      Id = 55,
      Email = "member@example.com",
      Username = "member",
      PasswordHash = "hash",
      Currency = "USD"
    };
    _mockAppleTokenValidator
      .Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<string?>()))
      .ReturnsAsync(new AppleTokenPayload
      {
        Subject = "apple-legit-sub",
        Email = existing.Email,
        EmailVerified = true
      });
    _mockUserRepository.Setup(r => r.GetByAppleIdAsync("apple-legit-sub")).ReturnsAsync((User?)null);
    _mockUserRepository.Setup(r => r.GetByEmailAsync(existing.Email)).ReturnsAsync(existing);
    _mockUserRepository.Setup(r => r.UpdateAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);

    var response = await _authService.AppleLoginAsync(new AppleLoginRequest
    {
      IdentityToken = "token",
      Nonce = "nonce-123"
    });

    response.Should().NotBeNull();
    response!.User.Id.Should().Be(existing.Id);
    existing.AppleId.Should().Be("apple-legit-sub");
    _mockUserRepository.Verify(r => r.CreateAsync(It.IsAny<User>()), Times.Never);
  }

  #endregion

  #region Account Deletion Tests

  [Fact]
  public async Task DeleteAccountAsync_RevokesPlaidItemsAndDeletesStripeCustomer()
  {
    // Arrange — a Google user (no password) with a Stripe customer and one bank connection
    var userId = 7;
    var user = new User
    {
      Id = userId,
      Username = "deleteme",
      Email = "deleteme@example.com",
      StripeCustomerId = "cus_test123"
    };
    _mockUserRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
    _context.PlaidConnections.Add(new PlaidConnection
    {
      Id = 55, UserId = userId, Provider = BankProvider.Plaid, ItemId = "item-55", AccessToken = "x", IsActive = true
    });
    await _context.SaveChangesAsync();

    // Act
    var result = await _authService.DeleteAccountAsync(userId);

    // Assert — deletion revokes at the processors, not just locally
    result.Should().BeTrue();
    _mockAggregator.Verify(a => a.DisconnectConnectionAsync(userId, 55), Times.Once);
    _mockBillingService.Verify(b => b.DeleteStripeCustomerAsync(userId), Times.Once);
    _mockDataRetentionManager.Verify(
      m => m.DeleteUserDataImmediatelyAsync(userId, It.IsAny<CancellationToken>()), Times.Once);
  }

  #endregion
}

/// <summary>
/// Simple meter factory for unit tests
/// </summary>
internal class TestMeterFactory : IMeterFactory
{
    public Meter Create(MeterOptions options) => new Meter(options.Name, options.Version);
    public void Dispose() { }
}
