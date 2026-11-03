using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using BuoyAI.API.Controllers;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.UnitTests.Controllers;

public class DebugControllerTests
{
  private readonly Mock<IAuthService> _mockAuthService;
  private readonly Mock<IUserRepository> _mockUserRepository;
  private readonly Mock<IHostEnvironment> _mockEnvironment;
  private readonly Mock<IConfiguration> _mockConfiguration;
  private readonly Mock<ILogger<DebugController>> _mockLogger;
  private readonly BuoyAIDbContext _dbContext;

  public DebugControllerTests()
  {
    _mockAuthService = new Mock<IAuthService>();
    _mockUserRepository = new Mock<IUserRepository>();
    _mockEnvironment = new Mock<IHostEnvironment>();
    _mockConfiguration = new Mock<IConfiguration>();
    _mockLogger = new Mock<ILogger<DebugController>>();

    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString())
      .Options;
    _dbContext = new BuoyAIDbContext(options);
  }

  private DebugController CreateController(string environment = "Development", string? apiKey = null, string? headerKey = null)
  {
#if !DEBUG
    // Release builds never skip the key, even in Development.
    if (environment == "Development" && apiKey == null) { apiKey = "dev-test-key"; headerKey = "dev-test-key"; }
#endif
    _mockEnvironment.Setup(e => e.EnvironmentName).Returns(environment);

    _mockConfiguration.Setup(c => c["Debug:ApiKey"]).Returns(apiKey);

    var controller = new DebugController(
      _mockAuthService.Object,
      _mockUserRepository.Object,
      _mockEnvironment.Object,
      _mockConfiguration.Object,
      _mockLogger.Object,
      _dbContext);

    var httpContext = new DefaultHttpContext();
    if (headerKey != null)
    {
      httpContext.Request.Headers["X-Debug-Key"] = headerKey;
    }

    controller.ControllerContext = new ControllerContext
    {
      HttpContext = httpContext
    };

    return controller;
  }

  [Fact]
  public async Task GetDebugToken_InProduction_ShouldReturn404()
  {
    var controller = CreateController(environment: "Production");

    var result = await controller.GetDebugToken(userId: 1, email: null);

    result.Should().BeOfType<NotFoundResult>();
  }

  [Fact]
  public async Task GetDebugToken_MissingBothParams_ShouldReturn400()
  {
    var controller = CreateController();

    var result = await controller.GetDebugToken(userId: null, email: null);

    var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
    badRequest.StatusCode.Should().Be(400);
  }

  [Fact]
  public async Task GetDebugToken_UnknownUserId_ShouldReturn404()
  {
    _mockUserRepository.Setup(r => r.GetByIdAsync(9999)).ReturnsAsync((User?)null);
    var controller = CreateController();

    var result = await controller.GetDebugToken(userId: 9999, email: null);

    result.Should().BeOfType<NotFoundObjectResult>();
  }

  [Fact]
  public async Task GetDebugToken_UnknownEmail_ShouldReturn404()
  {
    _mockUserRepository.Setup(r => r.GetByEmailAsync("nobody@example.com")).ReturnsAsync((User?)null);
    var controller = CreateController();

    var result = await controller.GetDebugToken(userId: null, email: "nobody@example.com");

    result.Should().BeOfType<NotFoundObjectResult>();
  }

  [Fact]
  public async Task GetDebugToken_ValidUserId_InDevelopment_ShouldReturnToken()
  {
    var user = new User { Id = 4, Email = "premium@example.com", Username = "premium", SubscriptionTier = SubscriptionTier.Premium };
    _mockUserRepository.Setup(r => r.GetByIdAsync(4)).ReturnsAsync(user);
    _mockAuthService.Setup(s => s.GenerateJwtTokenWithClaims(4, "premium", SubscriptionTier.Premium)).Returns("test-jwt-token");

    var controller = CreateController();

    var result = await controller.GetDebugToken(userId: 4, email: null);

    var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
    okResult.StatusCode.Should().Be(200);
  }

  [Fact]
  public async Task GetDebugToken_ValidEmail_InDevelopment_ShouldReturnToken()
  {
    var user = new User { Id = 2, Email = "test@example.com", Username = "testuser", SubscriptionTier = null };
    _mockUserRepository.Setup(r => r.GetByEmailAsync("test@example.com")).ReturnsAsync(user);
    _mockAuthService.Setup(s => s.GenerateJwtTokenWithClaims(2, "testuser", null)).Returns("test-jwt-token");

    var controller = CreateController();

    var result = await controller.GetDebugToken(userId: null, email: "test@example.com");

    var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
    okResult.StatusCode.Should().Be(200);
  }

  [Fact]
  public async Task GetDebugToken_InStaging_WithoutApiKey_ShouldReturn401()
  {
    var controller = CreateController(environment: "Staging", apiKey: "secret123", headerKey: null);

    var result = await controller.GetDebugToken(userId: 1, email: null);

    result.Should().BeOfType<UnauthorizedObjectResult>();
  }

  [Fact]
  public async Task GetDebugToken_InStaging_WithWrongApiKey_ShouldReturn401()
  {
    var controller = CreateController(environment: "Staging", apiKey: "secret123", headerKey: "wrong-key");

    var result = await controller.GetDebugToken(userId: 1, email: null);

    result.Should().BeOfType<UnauthorizedObjectResult>();
  }

  [Fact]
  public async Task GetDebugToken_InStaging_WithCorrectApiKey_ShouldReturnToken()
  {
    var user = new User { Id = 2, Email = "test@example.com", Username = "testuser", SubscriptionTier = SubscriptionTier.Premium };
    _mockUserRepository.Setup(r => r.GetByIdAsync(2)).ReturnsAsync(user);
    _mockAuthService.Setup(s => s.GenerateJwtTokenWithClaims(2, "testuser", SubscriptionTier.Premium)).Returns("test-jwt-token");

    var controller = CreateController(environment: "Staging", apiKey: "secret123", headerKey: "secret123");

    var result = await controller.GetDebugToken(userId: 2, email: null);

    var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
    okResult.StatusCode.Should().Be(200);
  }

  [Fact]
  public async Task GetDebugToken_InStaging_WithNoConfiguredKey_ShouldReturn401()
  {
    var controller = CreateController(environment: "Staging", apiKey: null, headerKey: "some-key");

    var result = await controller.GetDebugToken(userId: 1, email: null);

    result.Should().BeOfType<UnauthorizedObjectResult>();
  }

  [Fact]
  public void Ping_InProduction_ShouldReturn404()
  {
    var controller = CreateController(environment: "Production");

    var result = controller.Ping();

    result.Should().BeOfType<NotFoundResult>();
  }

  [Fact]
  public void Ping_InDevelopment_ShouldReturn200()
  {
    var controller = CreateController();

    var result = controller.Ping();

    result.Should().BeOfType<OkObjectResult>();
  }

  [Fact]
  public void Ping_InStaging_WithValidKey_ShouldReturn200()
  {
    var controller = CreateController(environment: "Staging", apiKey: "secret123", headerKey: "secret123");

    var result = controller.Ping();

    result.Should().BeOfType<OkObjectResult>();
  }

  [Fact]
  public void Ping_InStaging_WithoutKey_ShouldReturn401()
  {
    var controller = CreateController(environment: "Staging", apiKey: "secret123", headerKey: null);

    var result = controller.Ping();

    result.Should().BeOfType<UnauthorizedResult>();
  }
}
