using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The one-time token issued with an AI write preview: it works once, for its own user, until it
/// expires. POST /api/ai/tools/execute refuses anything else.
/// </summary>
public class AiToolSecurityServiceTests
{
  private readonly BuoyAIDbContext _db;
  private readonly AiToolSecurityService _service;

  public AiToolSecurityServiceTests()
  {
    _db = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    _service = new AiToolSecurityService(_db, Mock.Of<ILogger<AiToolSecurityService>>());
  }

  private Task<BuoyAI.Core.Entities.PendingToolConfirmation> Issue(int userId = 7) =>
    _service.CreatePendingConfirmationAsync(userId, "update_budget_amount",
      new Dictionary<string, object> { ["categoryId"] = 5, ["newAmount"] = 400 });

  [Fact]
  public async Task ValidToken_IsAcceptedOnce_WithTheStoredToolAndArguments()
  {
    var issued = await Issue();

    var used = await _service.ValidateAndConsumeTokenAsync(7, issued.ConfirmationToken);

    used.Should().NotBeNull();
    used!.ToolName.Should().Be("update_budget_amount");
    used.ArgumentsJson.Should().Contain("\"newAmount\":400");
  }

  [Fact]
  public async Task ReusedToken_IsRefused()
  {
    var issued = await Issue();
    await _service.ValidateAndConsumeTokenAsync(7, issued.ConfirmationToken);

    var again = await _service.ValidateAndConsumeTokenAsync(7, issued.ConfirmationToken);

    again.Should().BeNull();
  }

  [Fact]
  public async Task AnotherUsersToken_IsRefused()
  {
    var issued = await Issue(userId: 7);

    var stolen = await _service.ValidateAndConsumeTokenAsync(8, issued.ConfirmationToken);

    stolen.Should().BeNull();
  }

  [Fact]
  public async Task ExpiredToken_IsRefused()
  {
    var issued = await Issue();
    issued.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
    await _db.SaveChangesAsync();

    var late = await _service.ValidateAndConsumeTokenAsync(7, issued.ConfirmationToken);

    late.Should().BeNull();
  }

  [Fact]
  public async Task IssuedToken_LastsLongEnoughToReadTheCard()
  {
    var issued = await Issue();

    (issued.ExpiresAt - issued.CreatedAt).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(30));
  }
}
