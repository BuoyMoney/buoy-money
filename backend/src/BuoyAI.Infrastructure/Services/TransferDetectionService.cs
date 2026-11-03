using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Analysis;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.ValueObjects;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Surfaces likely internal transfers that were NOT tagged as transfers (so they inflate spending),
/// and lets the user confirm or dismiss them. Detection is pure (<see cref="TransferPairDetector"/>);
/// this service only fetches the data, resolves display names, and applies the user's decision.
/// Nothing is ever reclassified without an explicit confirm.
/// </summary>
public interface ITransferDetectionService
{
    Task<List<SuspectedTransferDto>> GetSuspectedTransfersAsync(int userId);

    /// <summary>Recategorize the given transactions as Transfer. Returns how many were updated.</summary>
    Task<int> ConfirmTransfersAsync(int userId, IReadOnlyList<int> transactionIds);

    /// <summary>Mark the given transactions reviewed (keeping their category) so they stop being suggested.</summary>
    Task<int> DismissTransfersAsync(int userId, IReadOnlyList<int> transactionIds);
}

public class TransferDetectionService : ITransferDetectionService
{
    // How far back to look for suspected transfers.
    private const int LookbackDays = 90;

    private readonly ITransactionRepository _transactionRepository;
    private readonly IAccountRepository _accountRepository;
    private readonly IUserRepository _userRepository;
    private readonly ITransactionService _transactionService;
    private readonly ISavingsService _savingsService;
    private readonly BuoyAIDbContext _dbContext;
    private readonly ILogger<TransferDetectionService> _logger;

    public TransferDetectionService(
        ITransactionRepository transactionRepository,
        IAccountRepository accountRepository,
        IUserRepository userRepository,
        ITransactionService transactionService,
        ISavingsService savingsService,
        BuoyAIDbContext dbContext,
        ILogger<TransferDetectionService> logger)
    {
        _transactionRepository = transactionRepository;
        _accountRepository = accountRepository;
        _userRepository = userRepository;
        _transactionService = transactionService;
        _savingsService = savingsService;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<List<SuspectedTransferDto>> GetSuspectedTransfersAsync(int userId)
    {
        var user = await _userRepository.GetByIdAsync(userId);
        var end = DateTime.UtcNow;
        var start = end.AddDays(-LookbackDays);

        var transactions = await _transactionRepository.GetByUserOrFamilyAsync(userId, user?.FamilyId, start, end);
        var accounts = (await _accountRepository.GetByUserOrFamilyAsync(userId, user?.FamilyId)).ToList();
        var accountNames = accounts.ToDictionary(a => a.Id, a => a.DisplayName);
        var accountOwners = accounts.ToDictionary(a => a.Id, a => a.UserId);

        var candidates = transactions
            .Select(t => new TransferCandidate(
                Id: t.Id,
                AccountId: t.AccountId,
                OwnerUserId: accountOwners.TryGetValue(t.AccountId, out var owner) ? owner : 0,
                AccountName: accountNames.TryGetValue(t.AccountId, out var name) ? name : $"Account {t.AccountId}",
                Amount: t.Amount,
                Type: t.Type,
                Date: t.Date,
                CategoryId: t.CategoryId,
                IsUserCategorized: t.IsUserCategorized,
                IsSplit: t.IsSplit,
                PlaidPrimary: t.PlaidCategoryPrimary,
                PlaidDetailed: t.PlaidCategoryDetailed,
                MerchantName: t.MerchantName,
                DismissedAsTransfer: t.TransferReviewedAt != null))
            .ToList();

        var suspects = TransferPairDetector.Detect(candidates);

        return suspects.Select(s => new SuspectedTransferDto
        {
            Confidence = s.Confidence.ToString().ToLowerInvariant(),
            Reason = s.Reason,
            Sides = s.Sides.Select(side => new SuspectedTransferSideDto
            {
                TransactionId = side.TransactionId,
                AccountId = side.AccountId,
                AccountName = side.AccountName,
                Amount = side.Amount,
                Date = side.Date,
                MerchantName = side.MerchantName
            }).ToList()
        }).ToList();
    }

    public async Task<int> ConfirmTransfersAsync(int userId, IReadOnlyList<int> transactionIds)
    {
        var ids = transactionIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return 0;
        }

        // All-or-nothing: a partially-confirmed pair (one leg Transfer, one not) is worse than none —
        // the confirmed leg drops out of the detector's eligible set, so the pair can never be
        // re-suggested and the surviving leg mis-states spending forever. Wrap every leg's mutation
        // (earmark reversal + unlink + recategorize) in one DB transaction so either all legs flip to
        // Transfer or none do. If any leg fails, roll back and return 0 to match the controller's
        // success = (updated == requested) contract.
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                await using var dbTransaction = await _dbContext.Database.BeginTransactionAsync();

                var updated = 0;
                foreach (var id in ids)
                {
                    // Load-then-check ownership; a leg that vanished or became inaccessible between
                    // the suggestion fetch and this confirm aborts the WHOLE pair (no half-apply).
                    var existing = await _transactionService.GetTransactionByIdAsync(id, userId);
                    if (existing == null)
                    {
                        throw new InvalidOperationException(
                            $"Transaction {id} not found or inaccessible; aborting transfer confirmation for user {userId}.");
                    }

                    // If the leg was earmarked to a savings goal, REVERSE the goal movement before
                    // unlinking — otherwise the deposit/withdrawal stays applied to the goal while the
                    // transaction is unlinked, permanently corrupting the goal balance. Mirrors the
                    // transaction category-update endpoint (reverse-then-unlink), not just a raw unlink.
                    if (existing.SavingsGoalId.HasValue)
                    {
                        await ReverseEarmarkAsync(existing);
                        await _transactionService.LinkToSavingsGoalAsync(id, null, userId);
                    }

                    await _transactionService.UpdateTransactionCategoryAsync(
                        id,
                        new UpdateTransactionCategoryRequest { CategoryId = (int)TransactionCategory.Transfer },
                        userId);
                    updated++;

                    // Detach everything this leg tracked before processing the next one. Each leg's
                    // category update leaves the transaction AND its refreshed Category navigation
                    // (UpdateTransactionCategoryAsync reloads CategoryNav for the returned DTO) tracked
                    // in the shared DbContext. Because both legs recategorize to the SAME (Transfer)
                    // category, the next leg's SaveChanges tries to track a SECOND Category instance
                    // with that key and EF throws an identity conflict ("another instance with the same
                    // key is already being tracked"), rolling the whole pair back so confirm never
                    // succeeds. This leg's writes are already flushed to the open transaction, so
                    // clearing the tracker is safe — the changes still commit below.
                    _dbContext.ChangeTracker.Clear();
                }

                await dbTransaction.CommitAsync();
                return updated;
            });
        }
        catch (Exception ex)
        {
            // Any failure rolled the whole batch back — report zero updated so the caller leaves the
            // suggestion in place rather than believing a partial pair was confirmed.
            _logger.LogWarning(ex, "Failed to confirm transfer pair for user {UserId}; rolled back all {Count} leg(s)", userId, ids.Count);
            return 0;
        }
    }

    public async Task<int> DismissTransfersAsync(int userId, IReadOnlyList<int> transactionIds)
    {
        var updated = 0;
        foreach (var id in transactionIds.Distinct())
        {
            try
            {
                // "Not a transfer" only means the user reviewed the transfer SUSPICION — it does NOT
                // mean the transaction is correctly categorized. Set a dedicated transfer-reviewed
                // marker (which the detector's eligibility filter consults) instead of forcing it
                // user-categorized; overloading IsUserCategorized would silently pull an uncategorized
                // transaction out of the normal categorization / AI-review queue.
                await _transactionService.MarkTransferReviewedAsync(id, userId);
                updated++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dismiss suspected transfer for transaction {TransactionId}, user {UserId}", id, userId);
            }
        }

        return updated;
    }

    /// <summary>
    /// Reverses an existing savings-goal earmark for the given transaction, picking the correct
    /// direction (undo deposit for income, undo withdrawal for expense). Mirrors the reverse-earmark
    /// helper in TransactionsController so confirming a transfer restores the goal balance instead of
    /// leaving the movement applied to an orphaned link. No-op if the transaction isn't earmarked.
    /// </summary>
    private async Task ReverseEarmarkAsync(TransactionDto transaction)
    {
        if (!transaction.SavingsGoalId.HasValue)
        {
            return;
        }

        var goalId = transaction.SavingsGoalId.Value;
        if (IsIncomeTransaction(transaction))
        {
            await _savingsService.UndoDepositAsync(goalId, transaction.Id);
        }
        else
        {
            await _savingsService.UndoWithdrawalAsync(goalId, transaction.Id);
        }
    }

    /// <summary>
    /// Whether a transaction counts as income (credit or an income category), which determines
    /// earmark direction. Mirrors TransactionsController.IsIncomeTransaction.
    /// </summary>
    private static bool IsIncomeTransaction(TransactionDto transaction)
        => transaction.Type == TransactionType.Credit
           || (transaction.CategoryId.HasValue
               && TransactionAmountExtensions.IsIncomeCategoryId(transaction.CategoryId.Value));
}
