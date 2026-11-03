using System.Threading.Tasks;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class StepUpAuthServiceTests
{
  private static BuoyAIDbContext CreateContext()
  {
    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(databaseName: $"stepup-{System.Guid.NewGuid()}")
      .Options;
    var context = new BuoyAIDbContext(options);
    context.Database.EnsureCreated();
    return context;
  }

  private static IConfiguration CreateConfiguration() =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?>
      {
        ["Jwt:Key"] = "this-is-a-sufficiently-long-test-signing-key-1234567890",
        ["Jwt:Issuer"] = "BuoyAI",
        ["Jwt:Audience"] = "BuoyAI-Users",
      })
      .Build();

  private static StepUpAuthService CreateService(BuoyAIDbContext context) =>
    new StepUpAuthService(
      context,
      CreateConfiguration(),
      new Mock<ITwoFactorService>().Object,
      new Mock<ILogger<StepUpAuthService>>().Object,
      new Mock<BuoyAI.Core.Interfaces.IEncryptionService>().Object);

  // Regression: the operation token writes a short "nameid" claim, but the default
  // JwtSecurityTokenHandler remaps inbound claims to long URIs on validation, so the
  // lookup missed and every freshly-issued token was rejected as "Invalid operation
  // token structure" — blocking the protected operation after a successful step-up.

  [Fact]
  public async Task ValidateOperationTokenAsync_AcceptsTokenFromGenerate_RoundTrip()
  {
    await using var context = CreateContext();
    var service = CreateService(context);
    const int userId = 12;
    const ProtectedOperation op = ProtectedOperation.ConnectBankAccount;

    var generated = await service.GenerateOperationTokenAsync(userId, op, 30);
    var isValid = await service.ValidateOperationTokenAsync(generated.OperationToken, op, userId);

    isValid.Should().BeTrue();
  }

  [Fact]
  public async Task ValidateOperationTokenAsync_RejectsTokenForDifferentUser()
  {
    await using var context = CreateContext();
    var service = CreateService(context);
    const ProtectedOperation op = ProtectedOperation.ConnectBankAccount;

    var generated = await service.GenerateOperationTokenAsync(12, op, 30);
    var isValid = await service.ValidateOperationTokenAsync(generated.OperationToken, op, 99);

    isValid.Should().BeFalse();
  }

  [Fact]
  public async Task ValidateOperationTokenAsync_RejectsTokenForDifferentOperation()
  {
    await using var context = CreateContext();
    var service = CreateService(context);

    var generated = await service.GenerateOperationTokenAsync(
      12, ProtectedOperation.ConnectBankAccount, 30);
    var isValid = await service.ValidateOperationTokenAsync(
      generated.OperationToken, ProtectedOperation.DeleteAccount, 12);

    isValid.Should().BeFalse();
  }
}
