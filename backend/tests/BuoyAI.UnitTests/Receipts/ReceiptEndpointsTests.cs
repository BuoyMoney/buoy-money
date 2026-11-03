using System.Text.Json;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.MCP.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Receipts;

public class SplitTransactionToolTests
{
  private readonly Mock<ITransactionService> _transactions = new();
  private readonly Mock<IUserRepository> _users = new();

  private TransactionWriteTools Tools() =>
    new(_transactions.Object, Mock.Of<ISavingsService>(), Mock.Of<IFamilyAuthorizationService>(), _users.Object, NullLogger<TransactionWriteTools>.Instance);

  private const string Parts = "[{\"amount\":40,\"category\":\"Groceries\",\"note\":\"Milk\"},{\"amount\":\"20.00\",\"category\":\"Personal Care\"}]";

  public SplitTransactionToolTests()
  {
    _transactions.Setup(t => t.GetTransactionByIdAsync(5, 1)).ReturnsAsync(new TransactionDto { Id = 5, Amount = -60m, MerchantName = "Walmart", Date = new DateTime(2026, 10, 1) });
  }

  [Fact]
  public async Task It_previews_before_splitting()
  {
    var json = await Tools().SplitTransactionAsync(1, 5, Parts);
    using var doc = JsonDocument.Parse(json);
    doc.RootElement.GetProperty("RequiresConfirmation").GetBoolean().Should().BeTrue();
    doc.RootElement.GetProperty("Description").GetString().Should().Contain("Groceries").And.Contain("PersonalCare");
    _transactions.Verify(t => t.SplitTransactionAsync(It.IsAny<int>(), It.IsAny<SplitTransactionRequest>(), It.IsAny<int>()), Times.Never);
  }

  [Fact]
  public async Task Confirmed_it_splits_with_notes()
  {
    SplitTransactionRequest? sent = null;
    _transactions
      .Setup(t => t.SplitTransactionAsync(5, It.IsAny<SplitTransactionRequest>(), 1))
      .Callback<int, SplitTransactionRequest, int>((_, r, _) => sent = r)
      .ReturnsAsync(new TransactionDto { Id = 5, Amount = -60m });

    var json = await Tools().SplitTransactionAsync(1, 5, Parts, confirmed: true);

    json.Should().Contain("\"Success\": true");
    sent!.Splits.Select(s => (s.Amount, s.CategoryId, s.Note)).Should().Equal((40m, (int?)20, "Milk"), (20m, (int?)27, (string?)null));
  }

  [Theory]
  [InlineData("[{\"amount\":40,\"category\":\"Groceries\"},{\"amount\":10,\"category\":\"Shopping\"}]", "add up to")]
  [InlineData("[{\"amount\":40,\"category\":\"Groceries\"},{\"amount\":19.99,\"category\":\"Shopping\"}]", "add up to")]
  [InlineData("[{\"amount\":60,\"category\":\"Groceries\"}]", "at least 2")]
  [InlineData("[{\"amount\":40,\"category\":\"Snacks\"},{\"amount\":20,\"category\":\"Shopping\"}]", "Unknown category")]
  [InlineData("not json", "at least 2")]
  public async Task Bad_parts_are_explained(string parts, string error)
  {
    (await Tools().SplitTransactionAsync(1, 5, parts, confirmed: true)).Should().Contain(error);
    _transactions.Verify(t => t.SplitTransactionAsync(It.IsAny<int>(), It.IsAny<SplitTransactionRequest>(), It.IsAny<int>()), Times.Never);
  }
}
