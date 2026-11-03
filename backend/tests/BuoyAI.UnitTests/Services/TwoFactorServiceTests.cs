using System;
using System.Linq;
using System.Threading.Tasks;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using OtpNet;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class TwoFactorServiceTests
{
  private static BuoyAIDbContext CreateContext()
  {
    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(databaseName: $"twofactor-{Guid.NewGuid()}")
      .Options;

    var context = new BuoyAIDbContext(options);
    context.Database.EnsureCreated();
    return context;
  }

  private static User CreateUser(int id, MfaType mfaType = MfaType.None)
  {
    return new User
    {
      Id = id,
      Username = $"user{id}",
      PasswordHash = "hash",
      Email = $"user{id}@example.com",
      FirstName = "Test",
      LastName = "User",
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      MfaType = mfaType,
      HasMfaEnabled = mfaType != MfaType.None,
      MfaEnabledAt = mfaType != MfaType.None ? DateTime.UtcNow : null,
      HasPasskeyRegistered = mfaType == MfaType.Passkey
    };
  }

  private static Mock<IUserRepository> CreateUserRepository(BuoyAIDbContext context)
  {
    var repo = new Mock<IUserRepository>();

    repo.Setup(r => r.GetByIdAsync(It.IsAny<int>()))
      .ReturnsAsync((int id) => context.Users.SingleOrDefault(u => u.Id == id));

    repo.Setup(r => r.GetByEmailAsync(It.IsAny<string>()))
      .ReturnsAsync((string email) => context.Users.SingleOrDefault(u => u.Email == email));

    repo.Setup(r => r.UpdateAsync(It.IsAny<User>()))
      .ReturnsAsync((User user) =>
      {
        context.SaveChanges();
        return user;
      });

    repo.Setup(r => r.CreateAsync(It.IsAny<User>()))
      .ReturnsAsync((User user) =>
      {
        context.Users.Add(user);
        context.SaveChanges();
        return user;
      });

    repo.Setup(r => r.DeleteAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
    repo.Setup(r => r.CountGoogleRegistrationsSinceAsync(It.IsAny<DateTime>())).ReturnsAsync(0);

    return repo;
  }

  private static TwoFactorService CreateService(
    BuoyAIDbContext context,
    Mock<IEmailService> emailService,
    Mock<IUserRepository> userRepository)
  {
    var logger = new Mock<ILogger<TwoFactorService>>();
    var options = Options.Create(new WebAuthnSettings());

    return new TwoFactorService(
      context,
      emailService.Object,
      userRepository.Object,
      logger.Object,
      options,
      Mock.Of<IEncryptionService>());
  }

  [Fact]
  public async Task GenerateAndSendCodeAsync_ShouldCreateNewTwoFactorCodeAndEmailUser()
  {
    await using var context = CreateContext();
    var user = CreateUser(1);
    context.Users.Add(user);
    context.TwoFactorCodes.Add(new TwoFactorCode
    {
      UserId = user.Id,
      Code = "123456",
      Purpose = "login",
      ExpiresAt = DateTime.UtcNow.AddMinutes(5),
      CreatedAt = DateTime.UtcNow.AddMinutes(-1),
      IsUsed = false
    });
    await context.SaveChangesAsync();

    var emailService = new Mock<IEmailService>();
    string? emailedCode = null;
    emailService
      .Setup(e => e.SendTwoFactorCodeAsync(user.Email, It.IsAny<string>(), "login", "127.0.0.1"))
      .Callback((string email, string code, string purpose, string? ip) => emailedCode = code)
      .Returns(Task.CompletedTask);

    var userRepository = CreateUserRepository(context);
    var service = CreateService(context, emailService, userRepository);

    var code = await service.GenerateAndSendCodeAsync(user.Id, "login", "127.0.0.1", "agent");

    code.Should().NotBeNullOrWhiteSpace();
    code.Should().HaveLength(6);
    code.Should().MatchRegex("\\d{6}");
    emailedCode.Should().Be(code);

    var previous = await context.TwoFactorCodes.FirstAsync();
    previous.IsUsed.Should().BeTrue();
    previous.UsedAt.Should().NotBeNull();

    var storedCode = await context.TwoFactorCodes
      .Where(c => c.Code == code)
      .SingleAsync();
    storedCode.UserAgent.Should().Be("agent");
    storedCode.IpAddress.Should().Be("127.0.0.1");

    emailService.Verify(e => e.SendTwoFactorCodeAsync(user.Email, code, "login", "127.0.0.1"), Times.Once);
  }

  [Fact]
  public async Task VerifyCodeAsync_ShouldReturnTrueForValidCode()
  {
    await using var context = CreateContext();
    var user = CreateUser(2);
    context.Users.Add(user);
    var code = new TwoFactorCode
    {
      UserId = user.Id,
      Code = "654321",
      Purpose = "login",
      ExpiresAt = DateTime.UtcNow.AddMinutes(5),
      CreatedAt = DateTime.UtcNow,
      IsUsed = false
    };
    context.TwoFactorCodes.Add(code);
    await context.SaveChangesAsync();

    var emailService = new Mock<IEmailService>();
    var userRepository = CreateUserRepository(context);
    var service = CreateService(context, emailService, userRepository);

    var result = await service.VerifyCodeAsync(user.Id, "654321", "login");

    result.Should().BeTrue();
    code.IsUsed.Should().BeTrue();
    code.UsedAt.Should().NotBeNull();
  }

  [Fact]
  public async Task VerifyCodeAsync_ShouldReturnFalseForExpiredCode()
  {
    await using var context = CreateContext();
    var user = CreateUser(3);
    context.Users.Add(user);
    context.TwoFactorCodes.Add(new TwoFactorCode
    {
      UserId = user.Id,
      Code = "000111",
      Purpose = "login",
      ExpiresAt = DateTime.UtcNow.AddMinutes(-1),
      CreatedAt = DateTime.UtcNow.AddMinutes(-10),
      IsUsed = false
    });
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var result = await service.VerifyCodeAsync(user.Id, "000111", "login");

    result.Should().BeFalse();
  }

  [Fact]
  public async Task InvalidateCodesAsync_ShouldMarkMatchingCodesAsUsed()
  {
    await using var context = CreateContext();
    var user = CreateUser(4);
    context.Users.Add(user);
    var loginCode = new TwoFactorCode
    {
      UserId = user.Id,
      Code = "111111",
      Purpose = "login",
      ExpiresAt = DateTime.UtcNow.AddMinutes(10),
      CreatedAt = DateTime.UtcNow,
      IsUsed = false
    };
    var resetCode = new TwoFactorCode
    {
      UserId = user.Id,
      Code = "222222",
      Purpose = "reset",
      ExpiresAt = DateTime.UtcNow.AddMinutes(10),
      CreatedAt = DateTime.UtcNow,
      IsUsed = false
    };
    context.TwoFactorCodes.AddRange(loginCode, resetCode);
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    await service.InvalidateCodesAsync(user.Id, "login");

    loginCode.IsUsed.Should().BeTrue();
    loginCode.UsedAt.Should().NotBeNull();
    resetCode.IsUsed.Should().BeFalse();
  }

  [Fact]
  public async Task RequiresUpgradeAsync_ShouldRespectMfaState()
  {
    await using var context = CreateContext();
    var totpUser = CreateUser(5, MfaType.Totp);
    var passkeyUser = CreateUser(6, MfaType.Passkey);
    var emailUser = CreateUser(7, MfaType.Email);
    emailUser.MfaEnabledAt = DateTime.UtcNow.AddDays(-40);
    var noneUser = CreateUser(8, MfaType.None);

    context.Users.AddRange(totpUser, passkeyUser, emailUser, noneUser);
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    (await service.RequiresUpgradeAsync(totpUser.Id)).Should().BeFalse();
    (await service.RequiresUpgradeAsync(passkeyUser.Id)).Should().BeFalse();
    (await service.RequiresUpgradeAsync(emailUser.Id)).Should().BeTrue();
    (await service.RequiresUpgradeAsync(noneUser.Id)).Should().BeTrue();
  }

  [Fact]
  public async Task CanAccessPlaidFeaturesAsync_PhishingResistantAlways_EmailOnlyInsideWindow()
  {
    await using var context = CreateContext();
    var totpUser = CreateUser(9, MfaType.Totp);
    var freshEmailUser = CreateUser(10, MfaType.Email); // MfaEnabledAt = now
    var passkeyUser = CreateUser(11, MfaType.Passkey);
    var lapsedEmailUser = CreateUser(13, MfaType.Email);
    lapsedEmailUser.MfaEnabledAt = DateTime.UtcNow.Subtract(TwoFactorService.EmailMfaPlaidWindow).AddDays(-1);

    context.Users.AddRange(totpUser, freshEmailUser, passkeyUser, lapsedEmailUser);
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    (await service.CanAccessPlaidFeaturesAsync(totpUser.Id)).Should().BeTrue();
    (await service.CanAccessPlaidFeaturesAsync(passkeyUser.Id)).Should().BeTrue();
    // Email is admitted for a bounded window after enrollment, then requires an upgrade.
    (await service.CanAccessPlaidFeaturesAsync(freshEmailUser.Id)).Should().BeTrue();
    (await service.CanAccessPlaidFeaturesAsync(lapsedEmailUser.Id)).Should().BeFalse();
  }

  [Fact]
  public async Task EnableEmailMfaAsync_ShouldUpdateUserFlags()
  {
    await using var context = CreateContext();
    var user = CreateUser(12, MfaType.None);
    context.Users.Add(user);
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    await service.EnableEmailMfaAsync(user.Id);

    user.MfaType.Should().Be(MfaType.Email);
    user.HasMfaEnabled.Should().BeTrue();
    user.MfaEnabledAt.Should().NotBeNull();
  }

  [Fact]
  public async Task EnableTotpMfaAsync_ShouldReturnSetupDetails()
  {
    await using var context = CreateContext();
    var user = CreateUser(13, MfaType.None);
    context.Users.Add(user);
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var setup = await service.EnableTotpMfaAsync(user.Id);

    setup.Secret.Should().NotBeNullOrWhiteSpace();
  setup.ManualEntryCode.Should().Contain(" ");
    setup.QrCodeUrl.Should().StartWith("data:image/png;base64,");
    user.TotpSecret.Should().Be(setup.Secret);
  }

  [Fact]
  public async Task CompleteTotpSetupAsync_ShouldEnableTotpAndReturnRecoveryCodes()
  {
    await using var context = CreateContext();
    var user = CreateUser(14, MfaType.None);
    context.Users.Add(user);
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var setup = await service.EnableTotpMfaAsync(user.Id);
    var totp = new Totp(Base32Encoding.ToBytes(setup.Secret));
    var verificationCode = totp.ComputeTotp(DateTime.UtcNow);

    var recoveryCodes = await service.CompleteTotpSetupAsync(user.Id, verificationCode);

    recoveryCodes.Should().HaveCount(8);
    recoveryCodes.Should().OnlyContain(code => code.Length == 8);
    user.MfaType.Should().Be(MfaType.Totp);
    user.HasMfaEnabled.Should().BeTrue();
    user.RecoveryCodes.Should().NotBeNull();
  }

  [Fact]
  public async Task GenerateRecoveryCodesAsync_ShouldPersistNewCodes()
  {
    await using var context = CreateContext();
    var user = CreateUser(15, MfaType.Totp);
    user.RecoveryCodes = "deadbeef";
    context.Users.Add(user);
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var codes = await service.GenerateRecoveryCodesAsync(user.Id);

    codes.Should().HaveCount(8);
    codes.Should().OnlyContain(code => code.Length == 8);
  user.RecoveryCodes.Should().Contain(";");
    user.RecoveryCodes!.Split(';').Should().BeEquivalentTo(codes);
  }

  [Fact]
  public async Task GetUserMfaDetailsAsync_ShouldReturnSnapshotOfUser()
  {
    await using var context = CreateContext();
    var user = CreateUser(16, MfaType.Email);
    user.MfaEnabledAt = DateTime.UtcNow.AddDays(-5);
    // Stale stored column: the details must report the COMPUTED window (from
    // MfaEnabledAt), which is what the Plaid gate actually enforces.
    user.MfaGracePeriodEndsAt = DateTime.UtcNow.AddDays(99);
    context.Users.Add(user);
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var details = await service.GetUserMfaDetailsAsync(user.Id);

    details.MfaType.Should().Be("email");
    details.HasMfaEnabled.Should().BeTrue();
    details.MfaGracePeriodEndsAt.Should().BeCloseTo(
      user.MfaEnabledAt!.Value.Add(TwoFactorService.EmailMfaPlaidWindow), TimeSpan.FromSeconds(1));
    details.HasPasskeyRegistered.Should().BeFalse();
  }

  [Fact]
  public async Task GenerateAndSendCodeAsync_WithNonExistentUser_ThrowsArgumentException()
  {
    await using var context = CreateContext();
    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var act = async () => await service.GenerateAndSendCodeAsync(999, "login");

    await act.Should().ThrowAsync<ArgumentException>().WithMessage("*User not found*");
  }

  [Fact]
  public async Task GenerateAndSendCodeAsync_MultipleCallsForSamePurpose_InvalidatesPreviousCodes()
  {
    await using var context = CreateContext();
    var user = CreateUser(17);
    context.Users.Add(user);
    await context.SaveChangesAsync();

    var emailService = new Mock<IEmailService>();
    emailService.Setup(e => e.SendTwoFactorCodeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
      .Returns(Task.CompletedTask);

    var service = CreateService(context, emailService, CreateUserRepository(context));

    var firstCode = await service.GenerateAndSendCodeAsync(user.Id, "login");

    // Age the first code past the resend cooldown so the next call issues a fresh one
    // (rather than throttling and returning the same code).
    var existing = await context.TwoFactorCodes
      .FirstAsync(c => c.UserId == user.Id && c.Purpose == "login");
    existing.CreatedAt = DateTime.UtcNow.AddMinutes(-2);
    await context.SaveChangesAsync();

    var secondCode = await service.GenerateAndSendCodeAsync(user.Id, "login");

    firstCode.Should().NotBe(secondCode);

    var allCodes = await context.TwoFactorCodes
      .Where(c => c.UserId == user.Id && c.Purpose == "login")
      .ToListAsync();

    allCodes.Should().HaveCount(2);
    allCodes.Count(c => c.IsUsed).Should().Be(1);
    allCodes.Single(c => !c.IsUsed).Code.Should().Be(secondCode);
  }

  [Fact]
  public async Task GenerateAndSendCodeAsync_WithinCooldown_ThrottlesAndReusesCode()
  {
    await using var context = CreateContext();
    var user = CreateUser(170);
    context.Users.Add(user);
    await context.SaveChangesAsync();

    var emailService = new Mock<IEmailService>();
    emailService.Setup(e => e.SendTwoFactorCodeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
      .Returns(Task.CompletedTask);
    var service = CreateService(context, emailService, CreateUserRepository(context));

    var first = await service.GenerateAndSendCodeAsync(user.Id, "login");
    var second = await service.GenerateAndSendCodeAsync(user.Id, "login"); // immediate resend

    second.Should().Be(first); // throttled — same code reused
    (await context.TwoFactorCodes.CountAsync(c => c.UserId == user.Id && c.Purpose == "login"))
      .Should().Be(1); // no extra row
    // Only one email sent despite two calls.
    emailService.Verify(
      e => e.SendTwoFactorCodeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
      Times.Once);
  }

  [Fact]
  public async Task VerifyCodeAsync_InvalidatesCodeAfterTooManyWrongAttempts()
  {
    await using var context = CreateContext();
    var user = CreateUser(171);
    context.Users.Add(user);
    context.TwoFactorCodes.Add(new TwoFactorCode
    {
      UserId = user.Id,
      Code = "123456",
      Purpose = "email_verification",
      ExpiresAt = DateTime.UtcNow.AddMinutes(10),
      CreatedAt = DateTime.UtcNow,
      IsUsed = false,
    });
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    // Five wrong guesses invalidate the code.
    for (var i = 0; i < 5; i++)
    {
      (await service.VerifyCodeAsync(user.Id, "000000", "email_verification")).Should().BeFalse();
    }

    // Even the correct code is now rejected — the user must request a new one.
    (await service.VerifyCodeAsync(user.Id, "123456", "email_verification")).Should().BeFalse();
  }

  [Fact]
  public async Task VerifyCodeAsync_WithWrongPurpose_ReturnsFalse()
  {
    await using var context = CreateContext();
    var user = CreateUser(18);
    context.Users.Add(user);
    context.TwoFactorCodes.Add(new TwoFactorCode
    {
      UserId = user.Id,
      Code = "123456",
      Purpose = "login",
      ExpiresAt = DateTime.UtcNow.AddMinutes(5),
      CreatedAt = DateTime.UtcNow,
      IsUsed = false
    });
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var result = await service.VerifyCodeAsync(user.Id, "123456", "reset");

    result.Should().BeFalse();
  }

  [Fact]
  public async Task VerifyCodeAsync_WithAlreadyUsedCode_ReturnsFalse()
  {
    await using var context = CreateContext();
    var user = CreateUser(19);
    context.Users.Add(user);
    context.TwoFactorCodes.Add(new TwoFactorCode
    {
      UserId = user.Id,
      Code = "654321",
      Purpose = "login",
      ExpiresAt = DateTime.UtcNow.AddMinutes(5),
      CreatedAt = DateTime.UtcNow,
      IsUsed = true,
      UsedAt = DateTime.UtcNow.AddMinutes(-1)
    });
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var result = await service.VerifyCodeAsync(user.Id, "654321", "login");

    result.Should().BeFalse();
  }

  [Fact]
  public async Task InvalidateCodesAsync_WithNoPurpose_InvalidatesAllCodes()
  {
    await using var context = CreateContext();
    var user = CreateUser(20);
    context.Users.Add(user);
    var loginCode = new TwoFactorCode
    {
      UserId = user.Id,
      Code = "111111",
      Purpose = "login",
      ExpiresAt = DateTime.UtcNow.AddMinutes(10),
      CreatedAt = DateTime.UtcNow,
      IsUsed = false
    };
    var resetCode = new TwoFactorCode
    {
      UserId = user.Id,
      Code = "222222",
      Purpose = "reset",
      ExpiresAt = DateTime.UtcNow.AddMinutes(10),
      CreatedAt = DateTime.UtcNow,
      IsUsed = false
    };
    context.TwoFactorCodes.AddRange(loginCode, resetCode);
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    await service.InvalidateCodesAsync(user.Id);

    loginCode.IsUsed.Should().BeTrue();
    resetCode.IsUsed.Should().BeTrue();
  }

  [Fact]
  public async Task RequiresUpgradeAsync_WithNonExistentUser_ReturnsFalse()
  {
    await using var context = CreateContext();
    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var result = await service.RequiresUpgradeAsync(999);

    result.Should().BeFalse();
  }

  [Fact]
  public async Task CanAccessPlaidFeaturesAsync_WithNonExistentUser_ReturnsFalse()
  {
    await using var context = CreateContext();
    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var result = await service.CanAccessPlaidFeaturesAsync(999);

    result.Should().BeFalse();
  }

  [Fact]
  public async Task EnableEmailMfaAsync_WithNonExistentUser_ThrowsArgumentException()
  {
    await using var context = CreateContext();
    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var act = async () => await service.EnableEmailMfaAsync(999);

    await act.Should().ThrowAsync<ArgumentException>().WithMessage("*User not found*");
  }

  [Fact]
  public async Task EnableTotpMfaAsync_WithNonExistentUser_ThrowsArgumentException()
  {
    await using var context = CreateContext();
    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var act = async () => await service.EnableTotpMfaAsync(999);

    await act.Should().ThrowAsync<ArgumentException>().WithMessage("*User not found*");
  }

  [Fact]
  public async Task CompleteTotpSetupAsync_WithoutInitiatedSetup_ThrowsInvalidOperationException()
  {
    await using var context = CreateContext();
    var user = CreateUser(21, MfaType.None);
    user.TotpSecret = null; // No TOTP setup initiated
    context.Users.Add(user);
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var act = async () => await service.CompleteTotpSetupAsync(user.Id, "123456");

    await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*TOTP setup not initiated*");
  }

  [Fact]
  public async Task CompleteTotpSetupAsync_WithInvalidCode_ThrowsInvalidOperationException()
  {
    await using var context = CreateContext();
    var user = CreateUser(22, MfaType.None);
    context.Users.Add(user);
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var setup = await service.EnableTotpMfaAsync(user.Id);

    var act = async () => await service.CompleteTotpSetupAsync(user.Id, "000000");

    await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Invalid verification code*");
  }

  [Fact]
  public async Task CompleteTotpSetupAsync_WithNonExistentUser_ThrowsArgumentException()
  {
    await using var context = CreateContext();
    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var act = async () => await service.CompleteTotpSetupAsync(999, "123456");

    await act.Should().ThrowAsync<ArgumentException>().WithMessage("*User not found*");
  }

  [Fact]
  public async Task GenerateRecoveryCodesAsync_WithNonExistentUser_ThrowsArgumentException()
  {
    await using var context = CreateContext();
    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var act = async () => await service.GenerateRecoveryCodesAsync(999);

    await act.Should().ThrowAsync<ArgumentException>().WithMessage("*User not found*");
  }

  [Fact]
  public async Task GenerateRecoveryCodesAsync_ReplacesOldCodes()
  {
    await using var context = CreateContext();
    var user = CreateUser(23, MfaType.Totp);
    user.RecoveryCodes = "oldcode1;oldcode2;oldcode3";
    context.Users.Add(user);
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var newCodes = await service.GenerateRecoveryCodesAsync(user.Id);

    newCodes.Should().HaveCount(8);
    user.RecoveryCodes.Should().NotContain("oldcode1");
  }

  [Fact]
  public async Task GetUserMfaDetailsAsync_WithNonExistentUser_ThrowsException()
  {
    await using var context = CreateContext();
    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var act = async () => await service.GetUserMfaDetailsAsync(999);

    // The service will throw when trying to access null user properties
    await act.Should().ThrowAsync<Exception>();
  }

  [Fact]
  public async Task VerifyCodeAsync_WithCorrectCode_MarksAsUsed()
  {
    await using var context = CreateContext();
    var user = CreateUser(24);
    context.Users.Add(user);
    var code = new TwoFactorCode
    {
      UserId = user.Id,
      Code = "999888",
      Purpose = "login",
      ExpiresAt = DateTime.UtcNow.AddMinutes(5),
      CreatedAt = DateTime.UtcNow,
      IsUsed = false
    };
    context.TwoFactorCodes.Add(code);
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var result = await service.VerifyCodeAsync(user.Id, "999888", "login");

    result.Should().BeTrue();
    code.IsUsed.Should().BeTrue();
    code.UsedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
  }

  [Fact]
  public async Task EnableTotpMfaAsync_GeneratesValidSecret()
  {
    await using var context = CreateContext();
    var user = CreateUser(25, MfaType.None);
    context.Users.Add(user);
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var setup = await service.EnableTotpMfaAsync(user.Id);

    setup.Secret.Should().NotBeNullOrWhiteSpace();
    setup.Secret.Length.Should().BeGreaterThan(16); // Base32 encoded secret should be longer
    setup.ManualEntryCode.Should().Contain(" "); // Should have spaces for manual entry
    setup.QrCodeUrl.Should().StartWith("data:image/png;base64,");
    user.TotpSecret.Should().Be(setup.Secret);
    user.MfaType.Should().Be(MfaType.None); // Not enabled until verification
  }

  [Fact]
  public async Task RequiresUpgradeAsync_WithRecentEmailMfa_ReturnsFalse()
  {
    await using var context = CreateContext();
    var user = CreateUser(26, MfaType.Email);
    user.MfaEnabledAt = DateTime.UtcNow.AddDays(-10); // Within 30-day grace period
    context.Users.Add(user);
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var result = await service.RequiresUpgradeAsync(user.Id);

    result.Should().BeFalse();
  }

  // ── Credential ID encoding normalization (passkey step-up regression) ──────
  // The Flutter "passkeys" plugin encodes the credential id differently for
  // registration vs. authentication (base64 vs. base64url, padded vs. not). The
  // backend must treat all of these as the same credential, otherwise step-up
  // passkey verification fails with "Credential not found or inactive".

  [Fact]
  public void NormalizeCredentialId_Base64AndBase64Url_ProduceSameCanonicalForm()
  {
    // Bytes that, when base64-encoded, contain both '+' and '/' (=> '-' and '_' in url form).
    var raw = new byte[] { 0xFB, 0xFF, 0x3E, 0x00, 0xFA, 0xCE, 0xFF };
    var standardPadded = Convert.ToBase64String(raw);                       // e.g. "+/8+APrO/w=="
    var urlUnpadded = standardPadded.TrimEnd('=').Replace('+', '-').Replace('/', '_');

    standardPadded.Should().NotBe(urlUnpadded); // sanity: the encodings differ as strings

    var a = TwoFactorService.NormalizeCredentialId(standardPadded);
    var b = TwoFactorService.NormalizeCredentialId(urlUnpadded);

    a.Should().Be(b);
    a.Should().Be(urlUnpadded); // canonical form is unpadded base64url
  }

  [Fact]
  public void NormalizeCredentialId_IsIdempotent()
  {
    var raw = new byte[] { 0x01, 0x02, 0x03, 0xAB, 0xCD, 0xEF, 0x10 };
    var once = TwoFactorService.NormalizeCredentialId(Convert.ToBase64String(raw));

    TwoFactorService.NormalizeCredentialId(once).Should().Be(once);
  }

  [Fact]
  public void CredentialIdVariants_MatchAcrossEncodings()
  {
    var raw = new byte[] { 0xFB, 0xFF, 0x3E, 0x00, 0xFA, 0xCE, 0xFF };
    var standardPadded = Convert.ToBase64String(raw);
    var urlUnpadded = standardPadded.TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // A lookup using the authentication-time encoding must contain the value a
    // registration could have stored (either canonical or the legacy raw form).
    var variantsFromUrl = TwoFactorService.CredentialIdVariants(urlUnpadded);
    var variantsFromStandard = TwoFactorService.CredentialIdVariants(standardPadded);

    variantsFromUrl.Should().Contain(standardPadded);
    variantsFromStandard.Should().Contain(urlUnpadded);
  }

  [Fact]
  public void NormalizeCredentialId_NonBase64Input_ReturnedUnchanged()
  {
    // Defensive: never throw on unexpected input.
    const string notBase64 = "!!! not base64 !!!";
    TwoFactorService.NormalizeCredentialId(notBase64).Should().Be(notBase64);
  }

  // ── Step-up scopes allowCredentials to the user's passkeys ─────────────────
  // Without this, an empty allowCredentials lets the platform authenticator return
  // any synced passkey for the relying party — including ones we never stored —
  // which then fails the lookup with "Credential not found".

  [Fact]
  public async Task InitiatePasskeyAuthenticationAsync_WithUserId_ScopesAllowCredentialsToUser()
  {
    await using var context = CreateContext();
    var user = CreateUser(30, MfaType.Passkey);
    context.Users.Add(user);
    context.UserPasskeys.Add(new UserPasskey
    {
      UserId = user.Id,
      CredentialId = "lxT8n9awjOyvbgknNxkiIA",
      PublicKey = "pubkey",
      Name = "Android",
      SignCount = 0,
      IsActive = true,
      CreatedAt = DateTime.UtcNow,
    });
    // An inactive credential for the same user must be excluded.
    context.UserPasskeys.Add(new UserPasskey
    {
      UserId = user.Id,
      CredentialId = "inactiveCred",
      PublicKey = "pubkey",
      Name = "Old",
      SignCount = 0,
      IsActive = false,
      CreatedAt = DateTime.UtcNow,
    });
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var result = await service.InitiatePasskeyAuthenticationAsync(
      new PasskeyAuthenticationInitDto { UserId = user.Id });

    result.AssertionOptions.Should().Contain("lxT8n9awjOyvbgknNxkiIA");
    result.AssertionOptions.Should().NotContain("inactiveCred");
    // Each allowCredentials entry must carry a "transports" field — the Flutter
    // passkeys plugin's generated deserializer throws if it is missing, which
    // aborts the ceremony before the biometric prompt.
    result.AssertionOptions.Should().Contain("transports");
  }

  [Fact]
  public async Task InitiatePasskeyAuthenticationAsync_WithoutUserId_LeavesAllowCredentialsEmpty()
  {
    await using var context = CreateContext();
    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var result = await service.InitiatePasskeyAuthenticationAsync(
      new PasskeyAuthenticationInitDto { Username = null });

    // Discoverable login: empty allowCredentials array.
    result.AssertionOptions.Should().Contain("\"allowCredentials\":[]");
  }

  [Fact]
  public async Task DisableMfaAsync_WithTotpEnabled_ClearsAllMfaState()
  {
    await using var context = CreateContext();
    var user = CreateUser(1, MfaType.Totp);
    user.TotpSecret = "JBSWY3DPEHPK3PXP";
    user.RecoveryCodes = "code-a;code-b;code-c";
    user.MfaGracePeriodEndsAt = DateTime.UtcNow.AddDays(30);
    context.Users.Add(user);
    // An outstanding email code should be invalidated by the disable.
    context.TwoFactorCodes.Add(new TwoFactorCode
    {
      UserId = user.Id,
      Code = "111222",
      Purpose = "login",
      ExpiresAt = DateTime.UtcNow.AddMinutes(5),
      CreatedAt = DateTime.UtcNow,
      IsUsed = false
    });
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    await service.DisableMfaAsync(user.Id);

    var updated = context.Users.Single(u => u.Id == user.Id);
    updated.HasMfaEnabled.Should().BeFalse();
    updated.MfaType.Should().Be(MfaType.None);
    updated.TotpSecret.Should().BeNull();
    updated.MfaEnabledAt.Should().BeNull();
    updated.MfaGracePeriodEndsAt.Should().BeNull();
    updated.RecoveryCodes.Should().BeNull();
    updated.HasPasskeyRegistered.Should().BeFalse();
    context.TwoFactorCodes.Single(c => c.UserId == user.Id).IsUsed.Should().BeTrue();
  }

  [Fact]
  public async Task DisableMfaAsync_WithPasskeys_RemovesAllPasskeys()
  {
    await using var context = CreateContext();
    var user = CreateUser(1, MfaType.Passkey);
    context.Users.Add(user);
    context.UserPasskeys.Add(new UserPasskey
    {
      UserId = user.Id,
      CredentialId = "cred-1",
      PublicKey = "pk-1",
      Name = "iPhone",
      CreatedAt = DateTime.UtcNow
    });
    context.UserPasskeys.Add(new UserPasskey
    {
      UserId = user.Id,
      CredentialId = "cred-2",
      PublicKey = "pk-2",
      Name = "Laptop",
      CreatedAt = DateTime.UtcNow
    });
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    await service.DisableMfaAsync(user.Id);

    context.UserPasskeys.Count(p => p.UserId == user.Id).Should().Be(0);
    var updated = context.Users.Single(u => u.Id == user.Id);
    updated.HasMfaEnabled.Should().BeFalse();
    updated.HasPasskeyRegistered.Should().BeFalse();
  }

  [Fact]
  public async Task DisableMfaAsync_WhenMfaNotEnabled_ThrowsInvalidOperation()
  {
    await using var context = CreateContext();
    var user = CreateUser(1); // MfaType.None, HasMfaEnabled == false
    context.Users.Add(user);
    await context.SaveChangesAsync();

    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    var act = () => service.DisableMfaAsync(user.Id);

    await act.Should().ThrowAsync<InvalidOperationException>()
      .WithMessage("*not currently enabled*");
  }

  // -- Plaid gate: TOTP/passkey always; email inside a 30-day window --

  private static async Task<bool> PlaidGateFor(MfaType type, DateTime? enabledAt)
  {
    await using var context = CreateContext();
    var user = CreateUser(1, type);
    user.MfaEnabledAt = enabledAt;
    context.Users.Add(user);
    await context.SaveChangesAsync();
    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));
    return await service.CanAccessPlaidFeaturesAsync(user.Id);
  }

  [Fact]
  public async Task CanAccessPlaidFeaturesAsync_Totp_ReturnsTrue()
    => (await PlaidGateFor(MfaType.Totp, DateTime.UtcNow.AddYears(-1))).Should().BeTrue();

  [Fact]
  public async Task CanAccessPlaidFeaturesAsync_Passkey_ReturnsTrue()
    => (await PlaidGateFor(MfaType.Passkey, DateTime.UtcNow.AddYears(-1))).Should().BeTrue();

  [Fact]
  public async Task CanAccessPlaidFeaturesAsync_EmailInsideWindow_ReturnsTrue()
    => (await PlaidGateFor(MfaType.Email, DateTime.UtcNow.AddDays(-29))).Should().BeTrue();

  [Fact]
  public async Task CanAccessPlaidFeaturesAsync_EmailWindowLapsed_ReturnsFalse()
    => (await PlaidGateFor(MfaType.Email, DateTime.UtcNow.AddDays(-31))).Should().BeFalse();

  [Fact]
  public async Task CanAccessPlaidFeaturesAsync_EmailWithoutEnabledAt_ReturnsFalse()
    => (await PlaidGateFor(MfaType.Email, null)).Should().BeFalse();

  [Fact]
  public async Task CanAccessPlaidFeaturesAsync_NoMfa_ReturnsFalse()
    => (await PlaidGateFor(MfaType.None, null)).Should().BeFalse();

  [Fact]
  public async Task RequiresUpgradeAsync_EmailInsideWindow_ReturnsFalse()
  {
    await using var context = CreateContext();
    var user = CreateUser(1, MfaType.Email);
    user.MfaEnabledAt = DateTime.UtcNow.AddDays(-10);
    context.Users.Add(user);
    await context.SaveChangesAsync();
    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    (await service.RequiresUpgradeAsync(user.Id)).Should().BeFalse();
  }

  [Fact]
  public async Task RequiresUpgradeAsync_EmailWindowLapsed_ReturnsTrue()
  {
    await using var context = CreateContext();
    var user = CreateUser(1, MfaType.Email);
    user.MfaEnabledAt = DateTime.UtcNow.AddDays(-31);
    context.Users.Add(user);
    await context.SaveChangesAsync();
    var service = CreateService(context, new Mock<IEmailService>(), CreateUserRepository(context));

    (await service.RequiresUpgradeAsync(user.Id)).Should().BeTrue();
  }
}
