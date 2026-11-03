using System.Security.Claims;
using System.Threading.Tasks;
using BuoyAI.API.Controllers;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Controllers;

public class UserControllerTests
{
  private readonly Mock<IAuthService> _authService = new();
  private readonly Mock<IUserRepository> _users = new();
  private readonly UserController _controller;

  public UserControllerTests()
  {
    _controller = new UserController(
      _authService.Object, _users.Object,
      new Mock<ILogger<UserController>>().Object,
      new Mock<IBudgetRepository>().Object,
      new Mock<IBudgetPeriodService>().Object);

    var httpContext = new DefaultHttpContext
    {
      User = new ClaimsPrincipal(new ClaimsIdentity(
          new[] { new Claim(ClaimTypes.NameIdentifier, "7") }, "test")),
    };
    _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
  }

  [Fact]
  public async Task UpdateProfile_WithValidNames_UpdatesAndReturnsProfile()
  {
    var user = new User { Id = 7, Email = "a@b.com", Username = "u", FirstName = "Old", LastName = "Name" };
    _users.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(user);
    _users.Setup(r => r.UpdateAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);
    _authService.Setup(a => a.GetUserProfileAsync(7)).ReturnsAsync(new UserProfileDto { Id = 7 });

    var result = await _controller.UpdateProfile(new UpdateProfileRequest { FirstName = "New", LastName = "Person" });

    result.Should().BeOfType<OkObjectResult>();
    user.FirstName.Should().Be("New");
    user.LastName.Should().Be("Person");
    _users.Verify(r => r.UpdateAsync(It.Is<User>(u => u.FirstName == "New" && u.LastName == "Person")), Times.Once);
  }

  [Fact]
  public async Task UpdateProfile_WithFirstNameOnly_Succeeds_AndKeepsExistingLastName()
  {
    // Apple / "Hide My Email" sign-ins provide no name; the welcome-name page sends
    // first name only. Last name is optional and must not be required (and must not
    // be wiped when omitted).
    var user = new User { Id = 7, Email = "a@b.com", Username = "u", FirstName = "", LastName = "Existing" };
    _users.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(user);
    _users.Setup(r => r.UpdateAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);
    _authService.Setup(a => a.GetUserProfileAsync(7)).ReturnsAsync(new UserProfileDto { Id = 7 });

    var result = await _controller.UpdateProfile(new UpdateProfileRequest { FirstName = "Jonathan" });

    result.Should().BeOfType<OkObjectResult>();
    user.FirstName.Should().Be("Jonathan");
    user.LastName.Should().Be("Existing"); // unchanged — lastName was null in the request
    _users.Verify(r => r.UpdateAsync(It.Is<User>(u => u.FirstName == "Jonathan")), Times.Once);
  }

  [Fact]
  public async Task UpdateProfile_WithMissingName_ReturnsBadRequestAndDoesNotUpdate()
  {
    _users.Setup(r => r.GetByIdAsync(7))
      .ReturnsAsync(new User { Id = 7, Email = "a@b.com", Username = "u" });

    var result = await _controller.UpdateProfile(new UpdateProfileRequest { FirstName = "  ", LastName = "" });

    result.Should().BeOfType<BadRequestObjectResult>();
    _users.Verify(r => r.UpdateAsync(It.IsAny<User>()), Times.Never);
  }

  [Fact]
  public async Task UpdateProfile_WhenUserNotFound_ReturnsNotFound()
  {
    _users.Setup(r => r.GetByIdAsync(7)).ReturnsAsync((User?)null);

    var result = await _controller.UpdateProfile(new UpdateProfileRequest { FirstName = "New", LastName = "Person" });

    result.Should().BeOfType<NotFoundObjectResult>();
    _users.Verify(r => r.UpdateAsync(It.IsAny<User>()), Times.Never);
  }

  // ── Avatar ────────────────────────────────────────────────────────────────

  private static IFormFile FakeImage(int bytes, string contentType)
  {
    var buffer = new byte[bytes];
    // Prefix a valid magic-byte signature so FileSignatureValidator accepts it (#647).
    // Only the real image types get a signature; unsupported types (e.g. image/gif) are
    // rejected by the content-type allow-list before the signature is ever checked.
    var signature = contentType switch
    {
      "image/png" => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
      "image/jpeg" => new byte[] { 0xFF, 0xD8, 0xFF },
      "image/webp" => new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50 },
      _ => Array.Empty<byte>(),
    };
    if (signature.Length <= bytes)
      Array.Copy(signature, buffer, signature.Length);

    var stream = new MemoryStream(buffer);
    return new FormFile(stream, 0, bytes, "file", "avatar")
    {
      Headers = new HeaderDictionary(),
      ContentType = contentType,
    };
  }

  [Fact]
  public async Task UploadAvatar_ValidImage_StoresAndReturnsProfile()
  {
    var user = new User { Id = 7, Email = "a@b.com", Username = "u" };
    _users.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(user);
    _users.Setup(r => r.UpdateAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);
    _authService.Setup(a => a.GetUserProfileAsync(7)).ReturnsAsync(new UserProfileDto { Id = 7 });

    var result = await _controller.UploadAvatar(FakeImage(1024, "image/png"));

    result.Should().BeOfType<OkObjectResult>();
    user.AvatarImage.Should().NotBeNull().And.HaveCount(1024);
    user.AvatarContentType.Should().Be("image/png");
    user.AvatarUpdatedAt.Should().NotBeNull();
  }

  [Fact]
  public async Task UploadAvatar_NoFile_ReturnsBadRequest()
  {
    var result = await _controller.UploadAvatar(null);
    result.Should().BeOfType<BadRequestObjectResult>();
    _users.Verify(r => r.UpdateAsync(It.IsAny<User>()), Times.Never);
  }

  [Fact]
  public async Task UploadAvatar_TooLarge_ReturnsBadRequest()
  {
    var result = await _controller.UploadAvatar(FakeImage(600 * 1024, "image/png"));
    result.Should().BeOfType<BadRequestObjectResult>();
    _users.Verify(r => r.UpdateAsync(It.IsAny<User>()), Times.Never);
  }

  [Fact]
  public async Task UploadAvatar_UnsupportedType_ReturnsBadRequest()
  {
    var result = await _controller.UploadAvatar(FakeImage(1024, "image/gif"));
    result.Should().BeOfType<BadRequestObjectResult>();
    _users.Verify(r => r.UpdateAsync(It.IsAny<User>()), Times.Never);
  }

  [Fact]
  public async Task GetAvatar_WhenSet_ReturnsImageFile()
  {
    _users.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(new User
    {
      Id = 7, Email = "a@b.com", Username = "u",
      AvatarImage = new byte[] { 1, 2, 3 }, AvatarContentType = "image/png",
    });

    var result = await _controller.GetAvatar(7);

    var file = result.Should().BeOfType<FileContentResult>().Subject;
    file.ContentType.Should().Be("image/png");
    file.FileContents.Should().HaveCount(3);
  }

  [Fact]
  public async Task GetAvatar_WhenNotSet_ReturnsNotFound()
  {
    _users.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(new User { Id = 7, Email = "a@b.com", Username = "u" });

    var result = await _controller.GetAvatar(7);

    result.Should().BeOfType<NotFoundResult>();
  }

  [Fact]
  public async Task DeleteAvatar_ClearsTheAvatar()
  {
    var user = new User
    {
      Id = 7, Email = "a@b.com", Username = "u",
      AvatarImage = new byte[] { 1 }, AvatarContentType = "image/png", AvatarUpdatedAt = DateTime.UtcNow,
    };
    _users.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(user);
    _users.Setup(r => r.UpdateAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);
    _authService.Setup(a => a.GetUserProfileAsync(7)).ReturnsAsync(new UserProfileDto { Id = 7 });

    var result = await _controller.DeleteAvatar();

    result.Should().BeOfType<OkObjectResult>();
    user.AvatarImage.Should().BeNull();
    user.AvatarContentType.Should().BeNull();
    user.AvatarUpdatedAt.Should().BeNull();
  }
}
