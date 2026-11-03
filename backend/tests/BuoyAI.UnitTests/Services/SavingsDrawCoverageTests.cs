using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using BuoyAI.Core.DTOs.Savings;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Interfaces.Repositories;
using BuoyAI.Core.ValueObjects;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Savings draws on a linked goal (0039): a purchase earmarked "paid from goal" on one account
/// is later settled by a transfer out of the goal's real account. These tests pin the three
/// behaviours the owner's Big O Tires case needed: a withdrawal adopts unassigned account money
/// instead of refusing, in-flight draws are reported, and a matching transfer settles them.
/// </summary>
public class SavingsDrawCoverageTests
{
  private readonly Mock<ISavingsRepository> _savingsRepository = new();
  private readonly Mock<IBudgetRepository> _budgetRepository = new();
  private readonly Mock<IUserRepository> _userRepository = new();
  private readonly Mock<IAccountRepository> _accountRepository = new();
  private readonly Mock<IFamilyAuthorizationService> _familyAuthService = new();
  private readonly Mock<IAIInsightGenerationService> _aiInsightService = new();
  private readonly Mock<IGamificationEvaluationQueue> _gamificationQueue = new();
  private readonly Mock<ITransactionRepository> _transactionRepository = new();

  private SavingsService CreateService() => new(
    _savingsRepository.Object,
    _budgetRepository.Object,
    _userRepository.Object,
    _accountRepository.Object,
    _familyAuthService.Object,
    _aiInsightService.Object,
    Mock.Of<ILogger<SavingsService>>(),
    _gamificationQueue.Object,
    Mock.Of<IGamificationService>(),
    _transactionRepository.Object);

  private static readonly DateTime LastSync = new(2026, 8, 20, 3, 14, 0, DateTimeKind.Utc);

  /// <summary>The owner's real shape: envelope 728.52 after an Aug 20 sync, bank now 1128.74.</summary>
  private SavingsGoal SetupEmergencyFund(decimal envelope = 728.52m, decimal balance = 1128.74m, int siblings = 1)
  {
    var account = new Account { Id = 5, UserId = 4, Name = "EMERGENCY", Type = AccountType.Savings, CurrentBalance = balance };
    var goal = new SavingsGoal
    {
      Id = 4, UserId = 4, Name = "Emergency Fund", TargetAmount = 15000m, CurrentAmount = envelope,
      AccountId = 5, Account = account,
      Allocations = new List<SavingsAllocation>
      {
        new() { Id = 55, SavingsGoalId = 4, AllocatedAmount = 477.52m, AllocationDate = LastSync, Source = SavingsService.SyncAllocationSource },
      },
    };
    _savingsRepository.Setup(r => r.GetByIdAsync(4)).ReturnsAsync(goal);
    _accountRepository.Setup(r => r.GetByIdAsync(5)).ReturnsAsync(account);
    _savingsRepository.Setup(r => r.GetGoalTotalsByAccountAsync(It.IsAny<IEnumerable<int>>()))
      .ReturnsAsync(() => new Dictionary<int, decimal> { [5] = goal.CurrentAmount + (siblings - 1) * 100m });
    _savingsRepository.Setup(r => r.GetGoalCountByAccountAsync(5)).ReturnsAsync(siblings);
    _savingsRepository.Setup(r => r.SaveGoalWithAllocationAsync(It.IsAny<SavingsGoal>(), It.IsAny<SavingsAllocation>()))
      .Returns(Task.CompletedTask);
    return goal;
  }

  private static Transaction Draw(int id, decimal amount, DateTime date, int goalId = 4, int accountId = 10) => new()
  {
    Id = id, AccountId = accountId, Amount = amount, Date = date, MerchantName = "Big O Tires",
    SavingsGoalId = goalId, Account = new Account { Id = accountId, Name = "CREDIT CARD" },
  };

  // ── Withdrawal adopts unassigned account money ─────────────────────────────

  [Fact]
  public async Task WithdrawFromGoalAsync_SoleGoalShortOfEnvelope_AdoptsUnassignedBalanceLikeASync()
  {
    var goal = SetupEmergencyFund();
    var service = CreateService();

    // $501.55 > $728.52 - $380.18 already drawn? No: envelope is 728.52 here; withdraw 881.73 (both rows).
    await service.WithdrawFromGoalAsync(4, 881.73m, transactionId: 1394);

    // Alone on the account, the goal adopts everything unassigned (1128.74 - 728.52 = 400.22)
    // and THEN withdraws, leaving the real remainder rather than a refusal.
    goal.CurrentAmount.Should().Be(1128.74m - 881.73m);
    _savingsRepository.Verify(r => r.SaveGoalWithAllocationAsync(
        It.IsAny<SavingsGoal>(),
        It.Is<SavingsAllocation>(a => a.Source == SavingsService.SyncAllocationSource && a.AllocatedAmount == 400.22m)),
      Times.Once);
    _savingsRepository.Verify(r => r.SaveGoalWithAllocationAsync(
        It.IsAny<SavingsGoal>(),
        It.Is<SavingsAllocation>(a => a.Source == "transaction_withdrawal:1394" && a.AllocatedAmount == -881.73m)),
      Times.Once);
  }

  [Fact]
  public async Task WithdrawFromGoalAsync_WithSiblings_AdoptsOnlyTheShortfall()
  {
    var goal = SetupEmergencyFund(envelope: 700m, balance: 1200m, siblings: 2);
    var service = CreateService();

    await service.WithdrawFromGoalAsync(4, 750m, transactionId: 77);

    // Unassigned = 1200 - (700 + 100 sibling) = 400 >= shortfall 50. With a sibling on the account
    // only the shortfall is adopted; the rest may be theirs.
    goal.CurrentAmount.Should().Be(0m);
    _savingsRepository.Verify(r => r.SaveGoalWithAllocationAsync(
        It.IsAny<SavingsGoal>(),
        It.Is<SavingsAllocation>(a => a.Source == SavingsService.SyncAllocationSource && a.AllocatedAmount == 50m)),
      Times.Once);
  }

  [Fact]
  public async Task WithdrawFromGoalAsync_AccountCannotCoverIt_StillRefuses()
  {
    SetupEmergencyFund(envelope: 300m, balance: 320m);
    var service = CreateService();

    var act = () => service.WithdrawFromGoalAsync(4, 500m, transactionId: 78);

    await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Insufficient savings balance*");
    _savingsRepository.Verify(r => r.SaveGoalWithAllocationAsync(It.IsAny<SavingsGoal>(), It.IsAny<SavingsAllocation>()), Times.Never);
  }

  [Fact]
  public async Task WithdrawFromGoalAsync_EnvelopeAlreadyCoversIt_DoesNotTouchTheAccount()
  {
    SetupEmergencyFund(envelope: 1000m, balance: 1128.74m);
    var service = CreateService();

    await service.WithdrawFromGoalAsync(4, 380.18m, transactionId: 1385);

    _accountRepository.Verify(r => r.GetByIdAsync(It.IsAny<int>()), Times.Never);
    _savingsRepository.Verify(r => r.SaveGoalWithAllocationAsync(It.IsAny<SavingsGoal>(), It.IsAny<SavingsAllocation>()), Times.Once);
  }

  // ── Pending draws ──────────────────────────────────────────────────────────

  [Fact]
  public async Task GetPendingDrawsAsync_ReturnsUncoveredDrawsSinceTheLastSyncOnly()
  {
    SetupEmergencyFund();
    _transactionRepository.Setup(r => r.GetUncoveredGoalWithdrawalsAsync(It.IsAny<IEnumerable<int>>()))
      .ReturnsAsync(new List<Transaction>
      {
        Draw(1385, -380.18m, new DateTime(2026, 9, 6, 0, 0, 0, DateTimeKind.Utc)),
        Draw(1394, -501.55m, new DateTime(2026, 9, 6, 0, 0, 0, DateTimeKind.Utc)),
        // Earmarked before the Aug 20 sync: the bank already absorbed it, not in flight.
        Draw(900, -40m, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)),
      });
    var service = CreateService();

    var pending = await service.GetPendingDrawsAsync(4);

    pending.Select(p => p.TransactionId).Should().Equal(1385, 1394);
    pending.Sum(p => p.Amount).Should().Be(881.73m);
    pending[0].Title.Should().Be("Big O Tires");
  }

  [Fact]
  public async Task GetPendingDrawsAsync_UnlinkedGoal_IsEmpty()
  {
    _savingsRepository.Setup(r => r.GetByIdAsync(5)).ReturnsAsync(new SavingsGoal { Id = 5, Name = "Vacation" });
    var service = CreateService();

    (await service.GetPendingDrawsAsync(5)).Should().BeEmpty();
    _transactionRepository.Verify(r => r.GetUncoveredGoalWithdrawalsAsync(It.IsAny<IEnumerable<int>>()), Times.Never);
  }

  // ── Auto-cover at ingest ───────────────────────────────────────────────────

  private List<Transaction> SetupTwoPendingDraws()
  {
    var goal = SetupEmergencyFund();
    _savingsRepository.Setup(r => r.GetByAccountIdAsync(5)).ReturnsAsync(new List<SavingsGoal> { goal });
    var draws = new List<Transaction>
    {
      Draw(1385, -380.18m, new DateTime(2026, 9, 6, 0, 0, 0, DateTimeKind.Utc)),
      Draw(1394, -501.55m, new DateTime(2026, 9, 6, 0, 0, 0, DateTimeKind.Utc)),
    };
    _transactionRepository.Setup(r => r.GetUncoveredGoalWithdrawalsAsync(It.IsAny<IEnumerable<int>>()))
      .ReturnsAsync(() => draws.Where(d => d.CoveringTransferTransactionId == null).ToList());
    _transactionRepository.Setup(r => r.UpdateAsync(It.IsAny<Transaction>())).ReturnsAsync((Transaction t) => t);
    return draws;
  }

  [Fact]
  public async Task AutoCoverPendingDrawsAsync_TransferEqualToAllPendingDraws_SettlesThemAll()
  {
    var draws = SetupTwoPendingDraws();
    var transfer = new Transaction { Id = 1500, AccountId = 5, Amount = -881.73m, Date = new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc) };
    var service = CreateService();

    var settled = await service.AutoCoverPendingDrawsAsync(new[] { transfer });

    settled.Should().Be(2);
    draws.Should().OnlyContain(d => d.CoveringTransferTransactionId == 1500);
    transfer.TransferReviewedAt.Should().NotBeNull("an explained transfer must not resurface as suspected");
  }

  [Fact]
  public async Task AutoCoverPendingDrawsAsync_TransferEqualToOneDraw_SettlesJustThatOne()
  {
    var draws = SetupTwoPendingDraws();
    var transfer = new Transaction { Id = 1501, AccountId = 5, Amount = -501.55m, Date = DateTime.UtcNow };
    var service = CreateService();

    var settled = await service.AutoCoverPendingDrawsAsync(new[] { transfer });

    settled.Should().Be(1);
    draws.Single(d => d.Id == 1394).CoveringTransferTransactionId.Should().Be(1501);
    draws.Single(d => d.Id == 1385).CoveringTransferTransactionId.Should().BeNull();
  }

  [Fact]
  public async Task AutoCoverPendingDrawsAsync_IgnoresInflowsPendingHoldsAndOtherAccounts()
  {
    var draws = SetupTwoPendingDraws();
    _savingsRepository.Setup(r => r.GetByAccountIdAsync(8)).ReturnsAsync(new List<SavingsGoal>());
    var rows = new[]
    {
      new Transaction { Id = 1, AccountId = 5, Amount = 881.73m, Date = DateTime.UtcNow },                     // inflow
      new Transaction { Id = 2, AccountId = 5, Amount = -881.73m, Date = DateTime.UtcNow, IsPending = true },  // hold
      new Transaction { Id = 3, AccountId = 8, Amount = -881.73m, Date = DateTime.UtcNow },                    // checking, no goals
      new Transaction { Id = 4, AccountId = 5, Amount = -100m, Date = DateTime.UtcNow },                       // no match
    };
    var service = CreateService();

    (await service.AutoCoverPendingDrawsAsync(rows)).Should().Be(0);
    draws.Should().OnlyContain(d => d.CoveringTransferTransactionId == null);
  }
}

/// <summary>Transaction-side rules for tying a draw to its covering transfer.</summary>
public class CoveringTransferLinkTests
{
  private static readonly Mock<IFamilyAuthorizationService> AllowAll = BuildAllowAll();

  private static Mock<IFamilyAuthorizationService> BuildAllowAll()
  {
    var m = new Mock<IFamilyAuthorizationService>();
    m.Setup(s => s.ValidateTransactionAccessOrThrowAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ResourcePermission>()))
      .Returns(Task.CompletedTask);
    return m;
  }

  private static TransactionService CreateService(Mock<ITransactionRepository> repo, Mock<ISavingsService> savings) => new(
    repo.Object,
    Mock.Of<IAccountRepository>(),
    Mock.Of<IUserRepository>(),
    Mock.Of<IBudgetRepository>(),
    Mock.Of<ICategoryRepository>(),
    Mock.Of<INotificationService>(),
    AllowAll.Object,
    Mock.Of<ISubscriptionTransactionRepository>(),
    Mock.Of<ITransactionEmbeddingService>(),
    Mock.Of<ILogger<TransactionService>>(),
    Mock.Of<IGamificationEvaluationQueue>(),
    Mock.Of<IGamificationService>(),
    new Lazy<ISavingsService>(() => savings.Object));

  private static Transaction Purchase(int id = 1385, decimal amount = -380.18m, int? goalId = 4) => new()
  {
    Id = id, AccountId = 10, Amount = amount, Date = new DateTime(2026, 9, 6, 0, 0, 0, DateTimeKind.Utc),
    MerchantName = "Big O Tires", SavingsGoalId = goalId,
    Account = new Account { Id = 10, UserId = 4, Name = "CREDIT CARD" },
    SavingsGoal = goalId.HasValue ? new SavingsGoal { Id = goalId.Value, Name = "Emergency Fund", AccountId = 5 } : null,
  };

  private static SavingsGoalDto LinkedGoal() => new() { Id = 4, Name = "Emergency Fund", AccountId = 5, AccountName = "EMERGENCY" };

  [Fact]
  public async Task LinkCoveringTransferAsync_TransferOnTheGoalsAccount_LinksAndMarksTheTransferReviewed()
  {
    var repo = new Mock<ITransactionRepository>();
    var savings = new Mock<ISavingsService>();
    var purchase = Purchase();
    var transfer = new Transaction { Id = 1500, AccountId = 5, Amount = -881.73m, Date = DateTime.UtcNow };
    repo.Setup(r => r.GetByIdWithSplitsAsync(1385)).ReturnsAsync(purchase);
    repo.Setup(r => r.GetByIdAsync(1500)).ReturnsAsync(transfer);
    repo.Setup(r => r.UpdateAsync(It.IsAny<Transaction>())).ReturnsAsync((Transaction t) => t);
    savings.Setup(s => s.GetSavingsGoalAsync(4, 4)).ReturnsAsync(LinkedGoal());
    var service = CreateService(repo, savings);

    await service.LinkCoveringTransferAsync(1385, new LinkCoveringTransferRequest { TransferTransactionId = 1500 }, userId: 4);

    purchase.CoveringTransferTransactionId.Should().Be(1500);
    transfer.TransferReviewedAt.Should().NotBeNull();
    savings.Verify(s => s.GetPendingDrawsAsync(It.IsAny<int>()), Times.Never, "IncludeOtherPending was not asked for");
  }

  [Fact]
  public async Task LinkCoveringTransferAsync_IncludeOtherPending_SettlesTheSiblingDrawToo()
  {
    var repo = new Mock<ITransactionRepository>();
    var savings = new Mock<ISavingsService>();
    var purchase = Purchase();
    var sibling = Purchase(id: 1394, amount: -501.55m);
    var transfer = new Transaction { Id = 1500, AccountId = 5, Amount = -881.73m, Date = DateTime.UtcNow };
    repo.Setup(r => r.GetByIdWithSplitsAsync(1385)).ReturnsAsync(purchase);
    repo.Setup(r => r.GetByIdAsync(1500)).ReturnsAsync(transfer);
    repo.Setup(r => r.GetByIdAsync(1394)).ReturnsAsync(sibling);
    repo.Setup(r => r.UpdateAsync(It.IsAny<Transaction>())).ReturnsAsync((Transaction t) => t);
    savings.Setup(s => s.GetSavingsGoalAsync(4, 4)).ReturnsAsync(LinkedGoal());
    savings.Setup(s => s.GetPendingDrawsAsync(4)).ReturnsAsync(new List<PendingDrawDto>
    {
      new() { TransactionId = 1385, Amount = 380.18m }, new() { TransactionId = 1394, Amount = 501.55m },
    });
    var service = CreateService(repo, savings);

    await service.LinkCoveringTransferAsync(1385,
      new LinkCoveringTransferRequest { TransferTransactionId = 1500, IncludeOtherPending = true }, userId: 4);

    sibling.CoveringTransferTransactionId.Should().Be(1500);
  }

  [Fact]
  public async Task LinkCoveringTransferAsync_TransferOnAnotherAccount_IsRefused()
  {
    var repo = new Mock<ITransactionRepository>();
    var savings = new Mock<ISavingsService>();
    var purchase = Purchase();
    repo.Setup(r => r.GetByIdWithSplitsAsync(1385)).ReturnsAsync(purchase);
    repo.Setup(r => r.GetByIdAsync(1600)).ReturnsAsync(new Transaction { Id = 1600, AccountId = 8, Amount = -881.73m });
    savings.Setup(s => s.GetSavingsGoalAsync(4, 4)).ReturnsAsync(LinkedGoal());
    var service = CreateService(repo, savings);

    var act = () => service.LinkCoveringTransferAsync(1385, new LinkCoveringTransferRequest { TransferTransactionId = 1600 }, 4);

    await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*isn't on EMERGENCY*");
    purchase.CoveringTransferTransactionId.Should().BeNull();
  }

  [Fact]
  public async Task LinkCoveringTransferAsync_GoalWithoutAnAccount_IsRefused()
  {
    var repo = new Mock<ITransactionRepository>();
    var savings = new Mock<ISavingsService>();
    repo.Setup(r => r.GetByIdWithSplitsAsync(1385)).ReturnsAsync(Purchase());
    savings.Setup(s => s.GetSavingsGoalAsync(4, 4)).ReturnsAsync(new SavingsGoalDto { Id = 4, Name = "Vacation Fund" });
    var service = CreateService(repo, savings);

    var act = () => service.GetCoveringTransferCandidatesAsync(1385, 4);

    await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*isn't linked to an account*");
  }

  [Fact]
  public async Task GetCoveringTransferCandidatesAsync_FlagsExactMatchesAndSortsThemFirst()
  {
    var repo = new Mock<ITransactionRepository>();
    var savings = new Mock<ISavingsService>();
    repo.Setup(r => r.GetByIdWithSplitsAsync(1385)).ReturnsAsync(Purchase());
    savings.Setup(s => s.GetSavingsGoalAsync(4, 4)).ReturnsAsync(LinkedGoal());
    savings.Setup(s => s.GetPendingDrawsAsync(4)).ReturnsAsync(new List<PendingDrawDto>
    {
      new() { TransactionId = 1385, Amount = 380.18m }, new() { TransactionId = 1394, Amount = 501.55m },
    });
    var sep = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    repo.Setup(r => r.GetByAccountIdAsync(5)).ReturnsAsync(new List<Transaction>
    {
      new() { Id = 1, AccountId = 5, Amount = 0.22m, Date = sep.AddDays(10) },                        // interest, inflow
      new() { Id = 2, AccountId = 5, Amount = -20m, Date = sep.AddDays(11) },                          // unrelated
      new() { Id = 3, AccountId = 5, Amount = -881.73m, Date = sep.AddDays(9), Description = "Transfer to checking" },
      new() { Id = 4, AccountId = 5, Amount = -380.18m, Date = sep.AddDays(8) },
      new() { Id = 5, AccountId = 5, Amount = -881.73m, Date = sep.AddDays(-30) },                     // too old
    });
    var service = CreateService(repo, savings);

    var result = await service.GetCoveringTransferCandidatesAsync(1385, 4);

    result.AccountName.Should().Be("EMERGENCY");
    result.PendingTotal.Should().Be(881.73m);
    result.Candidates.Select(c => c.TransactionId).Should().Equal(3, 4, 2);
    result.Candidates[0].MatchesPendingTotal.Should().BeTrue();
    result.Candidates[1].MatchesThis.Should().BeTrue();
    result.Candidates[2].MatchesThis.Should().BeFalse();
  }

  [Fact]
  public async Task LinkToSavingsGoalAsync_UnEarmarking_ClearsTheCoveringTransfer()
  {
    var repo = new Mock<ITransactionRepository>();
    var purchase = Purchase();
    purchase.CoveringTransferTransactionId = 1500;
    repo.Setup(r => r.GetByIdWithSplitsAsync(1385)).ReturnsAsync(purchase);
    repo.Setup(r => r.UpdateAsync(It.IsAny<Transaction>())).ReturnsAsync((Transaction t) => t);
    var service = CreateService(repo, new Mock<ISavingsService>());

    await service.LinkToSavingsGoalAsync(1385, null, 4);

    purchase.CoveringTransferTransactionId.Should().BeNull("nothing is in flight once the earmark is gone");
  }
}
