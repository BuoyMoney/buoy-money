using System.Security.Claims;
using System.Threading.Tasks;
using BuoyAI.API.Controllers;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Controllers;

public class EmailVerificationControllerTests
{
  private readonly Mock<ITwoFactorService> _twoFactor = new();
  private readonly Mock<IUserRepository> _users = new();
  private readonly EmailVerificationController _controller;

  public EmailVerificationControllerTests()
  {
    _controller = new EmailVerificationController(
      _twoFactor.Object, _users.Object,
      new Mock<ILogger<EmailVerificationController>>().Object);

    var httpContext = new DefaultHttpContext
    {
      User = new ClaimsPrincipal(new ClaimsIdentity(
          new[] { new Claim(ClaimTypes.NameIdentifier, "7") }, "test")),
    };
    _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
  }

  [Fact]
  public async Task ConfirmCode_WithValidCode_MarksEmailVerified()
  {
    var user = new User { Id = 7, Email = "a@b.com", Username = "u", IsEmailVerified = false };
    _twoFactor.Setup(t => t.VerifyCodeAsync(7, "123456", EmailVerificationController.Purpose))
      .ReturnsAsync(true);
    _users.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(user);
    _users.Setup(r => r.UpdateAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);

    var result = await _controller.ConfirmCode(new ConfirmEmailVerificationRequest { Code = "123456" });

    result.Should().BeOfType<OkObjectResult>();
    user.IsEmailVerified.Should().BeTrue();
    user.EmailVerifiedAt.Should().NotBeNull();
    _users.Verify(r => r.UpdateAsync(It.Is<User>(u => u.IsEmailVerified)), Times.Once);
  }

  [Fact]
  public async Task ConfirmCode_WhenNoMfaConfigured_EnablesEmailMfa()
  {
    // A user with no MFA who verifies their email gets email-code MFA for free —
    // the Settings "Email Code" setup and the dashboard "verify email" flow are unified.
    var user = new User
    {
      Id = 7,
      Email = "a@b.com",
      Username = "u",
      IsEmailVerified = false,
      MfaType = MfaType.None,
      HasMfaEnabled = false,
    };
    _twoFactor.Setup(t => t.VerifyCodeAsync(7, "123456", EmailVerificationController.Purpose))
      .ReturnsAsync(true);
    _users.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(user);
    _users.Setup(r => r.UpdateAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);

    var result = await _controller.ConfirmCode(new ConfirmEmailVerificationRequest { Code = "123456" });

    result.Should().BeOfType<OkObjectResult>();
    user.IsEmailVerified.Should().BeTrue();
    user.MfaType.Should().Be(MfaType.Email);
    user.HasMfaEnabled.Should().BeTrue();
    user.MfaEnabledAt.Should().NotBeNull();
  }

  [Fact]
  public async Task ConfirmCode_WhenPasskeyConfigured_DoesNotDowngradePrimaryMfaType()
  {
    // Verifying email must never weaken a stronger primary method. A passkey user keeps
    // MfaType.Passkey; we only ensure email is recorded as verified.
    var user = new User
    {
      Id = 7,
      Email = "a@b.com",
      Username = "u",
      IsEmailVerified = false,
      MfaType = MfaType.Passkey,
      HasMfaEnabled = true,
    };
    _twoFactor.Setup(t => t.VerifyCodeAsync(7, "123456", EmailVerificationController.Purpose))
      .ReturnsAsync(true);
    _users.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(user);
    _users.Setup(r => r.UpdateAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);

    var result = await _controller.ConfirmCode(new ConfirmEmailVerificationRequest { Code = "123456" });

    result.Should().BeOfType<OkObjectResult>();
    user.IsEmailVerified.Should().BeTrue();
    user.MfaType.Should().Be(MfaType.Passkey);
    user.HasMfaEnabled.Should().BeTrue();
  }

  [Fact]
  public async Task ConfirmCode_WithInvalidCode_ReturnsBadRequestAndDoesNotVerify()
  {
    _twoFactor.Setup(t => t.VerifyCodeAsync(7, "000000", EmailVerificationController.Purpose))
      .ReturnsAsync(false);

    var result = await _controller.ConfirmCode(new ConfirmEmailVerificationRequest { Code = "000000" });

    result.Should().BeOfType<BadRequestObjectResult>();
    _users.Verify(r => r.UpdateAsync(It.IsAny<User>()), Times.Never);
  }

  [Fact]
  public async Task SendCode_WhenAlreadyVerified_DoesNotSend()
  {
    _users.Setup(r => r.GetByIdAsync(7))
      .ReturnsAsync(new User { Id = 7, Email = "a@b.com", Username = "u", IsEmailVerified = true });

    var result = await _controller.SendCode();

    result.Should().BeOfType<OkObjectResult>();
    _twoFactor.Verify(
      t => t.GenerateAndSendCodeAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()),
      Times.Never);
  }

  [Fact]
  public async Task SendCode_WhenUnverified_SendsVerificationCode()
  {
    _users.Setup(r => r.GetByIdAsync(7))
      .ReturnsAsync(new User { Id = 7, Email = "a@b.com", Username = "u", IsEmailVerified = false });
    _twoFactor.Setup(t => t.GenerateAndSendCodeAsync(
        7, EmailVerificationController.Purpose, It.IsAny<string?>(), It.IsAny<string?>()))
      .ReturnsAsync("123456");

    var result = await _controller.SendCode();

    result.Should().BeOfType<OkObjectResult>();
    _twoFactor.Verify(
      t => t.GenerateAndSendCodeAsync(7, EmailVerificationController.Purpose, It.IsAny<string?>(), It.IsAny<string?>()),
      Times.Once);
  }
}
