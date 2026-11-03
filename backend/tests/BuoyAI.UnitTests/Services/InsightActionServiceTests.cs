using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.MCP;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Mscc.GenerativeAI.Types;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class InsightActionServiceTests
{
    private readonly Mock<IGeminiAgenticRunner> _runner = new();
    private readonly Mock<IMcpToolExecutor> _toolExecutor = new();
    private readonly Mock<ITransactionService> _transactionService = new();
    private readonly Mock<IFamilyService> _familyService = new();
    private readonly AIModelConfiguration _modelConfig;

    public InsightActionServiceTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = "test-signing-key-that-is-long-enough-1234567890" })
            .Build();
        _modelConfig = new AIModelConfiguration(config);

        _toolExecutor.Setup(t => t.GetAvailableTools()).Returns(new List<McpToolDefinition>
        {
            new() { Name = "get_transactions", ReadOnly = true },
            new() { Name = "categorize_transaction", ReadOnly = false },
        });
        _toolExecutor.Setup(t => t.GetGeminiFunctionDeclarations(It.IsAny<bool>(), It.IsAny<ISet<string>>()))
            .Returns(new List<FunctionDeclaration>());
    }

    private InsightActionService CreateService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = "test-signing-key-that-is-long-enough-1234567890" })
            .Build();

        return new InsightActionService(
            _runner.Object, _toolExecutor.Object, _transactionService.Object,
            _familyService.Object, _modelConfig, config, Mock.Of<ILogger<InsightActionService>>());
    }

    /// <summary>Wire the mocked runner to drive a single tool call through the real execute callback.</summary>
    private void RunnerInvokesTool(string toolName, Dictionary<string, object> args, string finalText = "Done.")
    {
        _runner.Setup(r => r.RunAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<FunctionDeclaration>>(),
                It.IsAny<Func<string, Dictionary<string, object>, Task<string>>>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (string m, string s, string u, IReadOnlyList<FunctionDeclaration> f,
                            Func<string, Dictionary<string, object>, Task<string>> exec, int max, CancellationToken ct) =>
            {
                await exec(toolName, args);
                return new AgenticRunResult(finalText, new List<AgenticToolCall>());
            });
    }

    [Fact]
    public async Task ExecuteHandoff_ReadOnlyUser_MakesNoChanges()
    {
        _familyService.Setup(f => f.CanUserModifyFinancialDataAsync(1)).ReturnsAsync(false);
        var service = CreateService();

        var result = await service.ExecuteHandoffAsync(1, "Recategorize transaction 5 as Transfer");

        result.Success.Should().BeFalse();
        result.CanUndo.Should().BeFalse();
        _runner.Verify(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<FunctionDeclaration>>(),
            It.IsAny<Func<string, Dictionary<string, object>, Task<string>>>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteHandoff_Categorize_CapturesInverseAndForcesConfirmed()
    {
        _familyService.Setup(f => f.CanUserModifyFinancialDataAsync(1)).ReturnsAsync(true);
        _transactionService.Setup(t => t.GetTransactionByIdAsync(5, 1)).ReturnsAsync(new TransactionDto
        {
            Id = 5, CategoryId = (int)TransactionCategory.Groceries, CategoryName = "Groceries", MerchantName = "Store"
        });
        Dictionary<string, object>? executedArgs = null;
        _toolExecutor.Setup(t => t.ExecuteConfirmedToolAsync(1, "categorize_transaction", It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .Callback((int _, string _, Dictionary<string, object>? a, CancellationToken _) => executedArgs = a)
            .ReturnsAsync("{\"Success\":true}");

        RunnerInvokesTool("categorize_transaction",
            new Dictionary<string, object> { ["transactionId"] = 5, ["category"] = "Transfer" },
            "Recategorized 1 transaction.");

        var service = CreateService();
        var result = await service.ExecuteHandoffAsync(1, "Recategorize transaction 5 as Transfer");

        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);
        result.UndoToken.Should().NotBeNullOrEmpty();
        // The button tap is the confirmation: the write goes through the user-confirmed path.
        executedArgs.Should().NotBeNull();
        _toolExecutor.Verify(t => t.ExecuteToolAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteHandoff_FailedWrite_IsNotRecordedForUndo()
    {
        _familyService.Setup(f => f.CanUserModifyFinancialDataAsync(1)).ReturnsAsync(true);
        _transactionService.Setup(t => t.GetTransactionByIdAsync(5, 1)).ReturnsAsync(new TransactionDto
        {
            Id = 5, CategoryId = (int)TransactionCategory.Groceries, CategoryName = "Groceries"
        });
        _toolExecutor.Setup(t => t.ExecuteConfirmedToolAsync(1, "categorize_transaction", It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"Error\":\"boom\"}");

        RunnerInvokesTool("categorize_transaction",
            new Dictionary<string, object> { ["transactionId"] = 5, ["category"] = "Transfer" });

        var service = CreateService();
        var result = await service.ExecuteHandoffAsync(1, "Recategorize 5");

        result.Changes.Should().BeEmpty();
        result.UndoToken.Should().BeNull();
    }

    [Fact]
    public async Task Undo_RestoresPriorCategory()
    {
        _familyService.Setup(f => f.CanUserModifyFinancialDataAsync(1)).ReturnsAsync(true);
        _transactionService.Setup(t => t.GetTransactionByIdAsync(5, 1)).ReturnsAsync(new TransactionDto
        {
            Id = 5, CategoryId = (int)TransactionCategory.Groceries, CategoryName = "Groceries"
        });
        _toolExecutor.Setup(t => t.ExecuteConfirmedToolAsync(1, "categorize_transaction", It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"Success\":true}");
        RunnerInvokesTool("categorize_transaction",
            new Dictionary<string, object> { ["transactionId"] = 5, ["category"] = "Transfer" });

        var service = CreateService();
        var act = await service.ExecuteHandoffAsync(1, "Recategorize 5 as Transfer");

        UpdateTransactionCategoryRequest? restore = null;
        _transactionService.Setup(t => t.UpdateTransactionCategoryAsync(5, It.IsAny<UpdateTransactionCategoryRequest>(), 1))
            .Callback((int _, UpdateTransactionCategoryRequest r, int _) => restore = r)
            .ReturnsAsync(new TransactionDto { Id = 5 });

        var undo = await service.UndoAsync(1, act.UndoToken!);

        undo.Success.Should().BeTrue();
        restore.Should().NotBeNull();
        restore!.CategoryId.Should().Be((int)TransactionCategory.Groceries); // prior category restored
    }

    [Fact]
    public async Task Undo_WrongUser_IsRejected()
    {
        _familyService.Setup(f => f.CanUserModifyFinancialDataAsync(It.IsAny<int>())).ReturnsAsync(true);
        _transactionService.Setup(t => t.GetTransactionByIdAsync(5, 1)).ReturnsAsync(new TransactionDto
        {
            Id = 5, CategoryId = (int)TransactionCategory.Groceries, CategoryName = "Groceries"
        });
        _toolExecutor.Setup(t => t.ExecuteConfirmedToolAsync(1, "categorize_transaction", It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"Success\":true}");
        RunnerInvokesTool("categorize_transaction",
            new Dictionary<string, object> { ["transactionId"] = 5, ["category"] = "Transfer" });

        var service = CreateService();
        var act = await service.ExecuteHandoffAsync(1, "Recategorize 5");

        // A different user must not be able to replay user 1's undo token.
        var undo = await service.UndoAsync(999, act.UndoToken!);

        undo.Success.Should().BeFalse();
        _transactionService.Verify(t => t.UpdateTransactionCategoryAsync(It.IsAny<int>(), It.IsAny<UpdateTransactionCategoryRequest>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Undo_ExpiredToken_IsRejected()
    {
        _familyService.Setup(f => f.CanUserModifyFinancialDataAsync(1)).ReturnsAsync(true);
        var service = CreateService();

        // A token issued two days ago is past the 24h window.
        var stale = service.SignToken(new InsightActionService.UndoPayload
        {
            UserId = 1,
            IssuedAtUnix = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds(),
            Ops = new List<InsightActionService.ReversalOp>
            {
                new() { Kind = InsightActionService.ReversalKind.SetTransactionCategory, TransactionId = 5, CategoryId = 1 },
            },
        });

        var undo = await service.UndoAsync(1, stale);

        undo.Success.Should().BeFalse();
        _transactionService.Verify(t => t.UpdateTransactionCategoryAsync(It.IsAny<int>(), It.IsAny<UpdateTransactionCategoryRequest>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Undo_TamperedToken_IsRejected()
    {
        _familyService.Setup(f => f.CanUserModifyFinancialDataAsync(1)).ReturnsAsync(true);
        var service = CreateService();

        var undo = await service.UndoAsync(1, "eyJVc2VySWQiOjF9.forgedsignature");

        undo.Success.Should().BeFalse();
    }
}
