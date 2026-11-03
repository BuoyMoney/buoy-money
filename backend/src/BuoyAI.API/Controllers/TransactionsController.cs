using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Extensions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.ValueObjects;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.API.Controllers;

[Authorize]
public class TransactionsController : BaseController
{
  private readonly ITransactionService _transactionService;
  private readonly ITransactionImportService _transactionImportService;
  private readonly ISavingsService _savingsService;
  private readonly IFamilyAuthorizationService _authService;
  private readonly ITransferDetectionService _transferDetectionService;
  private readonly ILogger<TransactionsController> _logger;

  public TransactionsController(
    ITransactionService transactionService,
    ITransactionImportService transactionImportService,
    ISavingsService savingsService,
    IFamilyAuthorizationService authService,
    ITransferDetectionService transferDetectionService,
    ILogger<TransactionsController> logger)
  {
    _transactionService = transactionService;
    _transactionImportService = transactionImportService;
    _savingsService = savingsService;
    _authService = authService;
    _transferDetectionService = transferDetectionService;
    _logger = logger;
  }

  /// <summary>
  /// Lists transactions that look like internal transfers but are counted as spending
  /// (matched debit/credit pairs across the user's own accounts, and Plaid-tagged transfers).
  /// Suggestions only — nothing is reclassified without an explicit confirm.
  /// </summary>
  [HttpGet("suspected-transfers")]
  public async Task<IActionResult> GetSuspectedTransfers()
  {
    var userId = GetCurrentUserId();
    var suspects = await _transferDetectionService.GetSuspectedTransfersAsync(userId);
    return Ok(new { suspectedTransfers = suspects });
  }

  /// <summary>Confirms suspected legs as an actual transfer (recategorizes them to Transfer).</summary>
  [HttpPost("suspected-transfers/confirm")]
  public async Task<IActionResult> ConfirmSuspectedTransfer([FromBody] ConfirmTransferRequest request)
  {
    if (request?.TransactionIds == null || request.TransactionIds.Count == 0)
    {
      return BadRequest(new { message = "At least one transaction id is required" });
    }

    var userId = GetCurrentUserId();
    var requested = request.TransactionIds.Distinct().Count();
    var updated = await _transferDetectionService.ConfirmTransfersAsync(userId, request.TransactionIds);
    // Require ALL legs to update — a partially-confirmed pair (one leg Transfer, one not) is worse
    // than none, so the client should surface a failure and leave the suggestion in place.
    return Ok(new { success = updated == requested, updated });
  }

  /// <summary>Dismisses a suspected transfer (marks the legs reviewed so they aren't suggested again).</summary>
  [HttpPost("suspected-transfers/dismiss")]
  public async Task<IActionResult> DismissSuspectedTransfer([FromBody] DismissTransferRequest request)
  {
    if (request?.TransactionIds == null || request.TransactionIds.Count == 0)
    {
      return BadRequest(new { message = "At least one transaction id is required" });
    }

    var userId = GetCurrentUserId();
    var updated = await _transferDetectionService.DismissTransfersAsync(userId, request.TransactionIds);
    return Ok(new { success = updated > 0, updated });
  }

  [HttpGet]
  public async Task<IActionResult> GetTransactions([FromQuery] TransactionFilterRequest filter)
  {
    var userId = GetCurrentUserId();

    // Validate filter parameters
    if (!filter.IsDateRangeValid())
    {
      return BadRequest(new { message = "Invalid date range: start date must be before or equal to end date" });
    }

    var response = await _transactionService.GetTransactionsWithPaginationAsync(userId, filter);

    return Ok(response);
  }

  [HttpGet("summary")]
  public async Task<IActionResult> GetTransactionSummary([FromQuery] DateTime? startDate, [FromQuery] DateTime? endDate)
  {
    var userId = GetCurrentUserId();

    // Convert dates to UTC using helper
    var (effectiveStartDate, effectiveEndDate) = DateTimeExtensions.GetEffectiveDateRange(startDate, endDate);

    // Validate date range
    if (!DateTimeExtensions.IsValidDateRange(effectiveStartDate, effectiveEndDate))
    {
      return BadRequest(new { message = "Start date must be before or equal to end date" });
    }

    var summary = await _transactionService.GetTransactionSummaryAsync(userId, effectiveStartDate, effectiveEndDate);

    return Ok(summary);
  }

  /// <summary>
  /// Actual cash flow for a period: money received, money spent, and the net — with credit-card
  /// payments and internal transfers excluded from both sides and refunds netted. This is the
  /// truthful "what came in and went out", distinct from budget allocations (what was planned).
  /// </summary>
  [HttpGet("cash-flow")]
  public async Task<IActionResult> GetCashFlow([FromQuery] DateTime? startDate, [FromQuery] DateTime? endDate)
  {
    var userId = GetCurrentUserId();
    var (effectiveStartDate, effectiveEndDate) = DateTimeExtensions.GetEffectiveDateRange(startDate, endDate);

    if (!DateTimeExtensions.IsValidDateRange(effectiveStartDate, effectiveEndDate))
    {
      return BadRequest(new { message = "Start date must be before or equal to end date" });
    }

    // CashFlowCalculator treats the end as exclusive; the effective range is inclusive, so extend
    // by a day to include the whole final day.
    var summary = await _transactionService.GetCashFlowSummaryAsync(
      userId, effectiveStartDate, effectiveEndDate.AddDays(1));

    return Ok(summary);
  }

  [HttpGet("categories")]
  public async Task<IActionResult> GetCategoryStatistics([FromQuery] DateTime? startDate, [FromQuery] DateTime? endDate)
  {
    var userId = GetCurrentUserId();

    // Convert dates to UTC using helper
    var (effectiveStartDate, effectiveEndDate) = DateTimeExtensions.GetEffectiveDateRange(startDate, endDate);

    // Validate date range
    if (!DateTimeExtensions.IsValidDateRange(effectiveStartDate, effectiveEndDate))
    {
      return BadRequest(new { message = "Start date must be before or equal to end date" });
    }

    var statistics = await _transactionService.GetCategoryStatisticsAsync(userId, effectiveStartDate, effectiveEndDate);

    return Ok(statistics);
  }

  [HttpPut("{id}/category")]
  public async Task<IActionResult> UpdateTransactionCategory(int id, [FromBody] UpdateTransactionCategoryRequest request)
  {
    try
    {
      var userId = GetCurrentUserId();

      // Moving an earmarked transaction back to a normal budget category must
      // reverse the savings-goal movement first, so the money returns to the
      // budget / leftover pool. Done server-side so the invariant holds no matter
      // which client calls this (not just the Flutter picker).
      var existing = await _transactionService.GetTransactionByIdAsync(id, userId);
      if (existing == null)
        return NotFound(new { message = "Transaction not found or you don't have permission to modify it" });

      if (existing.SavingsGoalId.HasValue)
      {
        await ReverseEarmarkAsync(existing);
        await _transactionService.LinkToSavingsGoalAsync(id, null, userId);
      }

      var transaction = await _transactionService.UpdateTransactionCategoryAsync(id, request, userId);

      return Ok(transaction);
    }
    catch (ArgumentException ex)
    {
      _logger.LogWarning(ex, "Transaction not found or user not authorized for transaction {TransactionId}", id);
      return NotFound(new { message = "Transaction not found or you don't have permission to modify it" });
    }
  }

  [HttpPost("reset-and-resync")]
  public async Task<IActionResult> ResetAndResyncTransactions()
  {
    var userId = GetCurrentUserId();
    await _transactionService.ResetAndResyncTransactionsAsync(userId);

    return Ok(new { message = "Transactions reset successfully. Background sync will repopulate your transactions with corrected data." });
  }

  [HttpGet("{id}")]
  public async Task<IActionResult> GetTransaction(int id)
  {
    var userId = GetCurrentUserId();
    var transaction = await _transactionService.GetTransactionByIdAsync(id, userId);

    if (transaction == null)
      return NotFound(new { message = "Transaction not found or you don't have permission to view it" });

    return Ok(transaction);
  }

  [HttpPut("{id}/notes")]
  public async Task<IActionResult> UpdateTransactionNotes(int id, [FromBody] UpdateTransactionNotesRequest request)
  {
    try
    {
      var userId = GetCurrentUserId();
      var transaction = await _transactionService.UpdateTransactionNotesAsync(id, request.Notes, userId);

      return Ok(transaction);
    }
    catch (ArgumentException ex)
    {
      _logger.LogWarning(ex, "Transaction not found or user not authorized for transaction {TransactionId}", id);
      return NotFound(new { message = "Transaction not found or you don't have permission to modify it" });
    }
  }

  /// <summary>
  /// Rename a transaction. Null/empty clears the user's name, falling back to the bank's own
  /// text. Stored alongside Description rather than overwriting it — see Transaction.Nickname.
  /// </summary>
  [HttpPut("{id}/nickname")]
  public async Task<IActionResult> UpdateTransactionNickname(int id, [FromBody] UpdateTransactionNicknameRequest request)
  {
    try
    {
      var userId = GetCurrentUserId();
      var transaction = await _transactionService.UpdateTransactionNicknameAsync(id, request.Nickname, userId);

      return Ok(transaction);
    }
    catch (InvalidOperationException ex)
    {
      _logger.LogWarning(ex, "Invalid name for transaction {TransactionId}", id);
      return BadRequest(new { message = ex.Message });
    }
    catch (ArgumentException ex)
    {
      _logger.LogWarning(ex, "Could not rename transaction {TransactionId}", id);
      return NotFound(new { message = "Transaction not found or you don't have permission to modify it" });
    }
  }

  [HttpPost("{id}/split")]
  public async Task<IActionResult> SplitTransaction(int id, [FromBody] SplitTransactionRequest request)
  {
    try
    {
      var userId = GetCurrentUserId();
      var transaction = await _transactionService.SplitTransactionAsync(id, request, userId);

      return Ok(transaction);
    }
    catch (ArgumentException ex)
    {
      _logger.LogWarning(ex, "Invalid split request for transaction {TransactionId}: {Message}", id, ex.Message);
      return BadRequest(new { message = ex.Message });
    }
  }

  [HttpPost("{id}/unsplit")]
  public async Task<IActionResult> UnsplitTransaction(int id)
  {
    try
    {
      var userId = GetCurrentUserId();
      var transaction = await _transactionService.UnsplitTransactionAsync(id, userId);

      return Ok(transaction);
    }
    catch (ArgumentException ex)
    {
      _logger.LogWarning(ex, "Cannot unsplit transaction {TransactionId}: {Message}", id, ex.Message);
      return BadRequest(new { message = ex.Message });
    }
  }

  /// <summary>
  /// Create a manual transaction (user-entered, not from Plaid or file import).
  /// </summary>
  [HttpPost]
  public async Task<IActionResult> CreateTransaction([FromBody] CreateTransactionRequest request)
  {
    try
    {
      var userId = GetCurrentUserId();
      var transaction = await _transactionService.CreateManualTransactionAsync(request, userId);
      return Ok(transaction);
    }
    catch (ArgumentException ex)
    {
      _logger.LogWarning(ex, "Invalid manual transaction request: {Message}", ex.Message);
      return BadRequest(new { message = ex.Message });
    }
    catch (UnauthorizedAccessException ex)
    {
      _logger.LogWarning(ex, "Unauthorized manual transaction create attempt");
      return StatusCode(403, new { message = ex.Message });
    }
  }

  [HttpDelete("{id}")]
  public async Task<IActionResult> DeleteTransaction(int id)
  {
    try
    {
      var userId = GetCurrentUserId();
      await _transactionService.DeleteTransactionAsync(id, userId);

      return NoContent();
    }
    catch (ArgumentException ex)
    {
      _logger.LogWarning(ex, "Transaction not found or user not authorized for transaction {TransactionId}", id);
      return NotFound(new { message = "Transaction not found or you don't have permission to delete it" });
    }
  }

  [HttpPut("{id}/date")]
  public async Task<IActionResult> UpdateTransactionDate(int id, [FromBody] UpdateTransactionDateRequest request)
  {
    try
    {
      var userId = GetCurrentUserId();
      var transaction = await _transactionService.UpdateTransactionDateAsync(id, request.Date, userId);

      return Ok(transaction);
    }
    catch (ArgumentException ex)
    {
      _logger.LogWarning(ex, "Transaction not found or user not authorized for transaction {TransactionId}", id);
      return NotFound(new { message = "Transaction not found or you don't have permission to modify it" });
    }
  }

  /// <summary>
  /// Import transactions from a file (OFX, QFX, or CSV).
  /// </summary>
  /// <param name="accountId">The target account ID to import transactions into</param>
  /// <param name="file">The file to import (OFX, QFX, or CSV format)</param>
  /// <returns>Import result with counts of imported, skipped duplicates, and errors</returns>
  [HttpPost("import")]
  [RequestSizeLimit(5_000_000)] // 5MB max
  public async Task<IActionResult> ImportTransactions([FromForm] int accountId, IFormFile file)
  {
    var userId = GetCurrentUserId();

    if (file == null || file.Length == 0)
    {
      return BadRequest(new { message = "No file provided" });
    }

    // Validate file extension
    var fileName = file.FileName;
    if (!AllowedTransactionImportTypes.IsAllowedExtension(fileName))
    {
      return BadRequest(new { message = "Invalid file type. Allowed: .ofx, .qfx, .csv" });
    }

    // Validate file size
    if (file.Length > AllowedTransactionImportTypes.MaxFileSizeBytes)
    {
      return BadRequest(new { message = "File too large. Maximum size is 5MB." });
    }

    using var stream = file.OpenReadStream();
    var result = await _transactionImportService.ImportFromFileAsync(userId, accountId, stream, fileName);

    if (!result.Success)
    {
      return BadRequest(result);
    }

    return Ok(result);
  }

  /// <summary>
  /// Earmark a transaction to a savings goal (sign-aware).
  /// An income transaction (credit / income category) is a DEPOSIT into the goal;
  /// an expense (debit) is a WITHDRAWAL from it. Either way the transaction is
  /// excluded from budget totals so the money is tracked against the goal, not the
  /// budget. Route kept as "savings-withdrawal" for client/back-compat.
  /// </summary>
  [HttpPost("{id}/savings-withdrawal")]
  public async Task<IActionResult> ApplyToSavingsGoal(int id, [FromBody] MarkSavingsWithdrawalRequest request)
  {
    try
    {
      var userId = GetCurrentUserId();

      // Verify transaction exists and belongs to user
      var transaction = await _transactionService.GetTransactionByIdAsync(id, userId);
      if (transaction == null)
        return NotFound(new { message = "Transaction not found" });

      // Parent split transactions carry no real amount of their own — their
      // children do. Earmarking one would be ambiguous accounting.
      if (transaction.IsSplit)
        return BadRequest(new { message = "Split transactions can't be earmarked to a savings goal. Earmark the individual split items instead." });

      // A pending hold's amount still changes when it posts (a $284 grocery hold often
      // settles at a different figure), and the Plaid sync overwrites that amount in place.
      // Earmarking a pending row now would move the goal balance by an amount that later
      // drifts from the real charge — and the sync never reconciles the earmark to the new
      // amount. It also marks the hold "touched", so on institutions that don't link
      // hold→charge the stale-pending cleanup can no longer dedupe it and both rows persist.
      // Require the charge to post before it can be earmarked.
      if (transaction.IsPending)
        return BadRequest(new { message = "This transaction is still pending. Once it posts you can earmark it to a savings goal." });

      // Authorize access to the target savings goal (prevents linking to another user's goal)
      var authResult = await _authService.CanAccessGoalAsync(userId, request.SavingsGoalId, ResourcePermission.Edit);
      if (!authResult.IsAuthorized)
      {
        if (authResult.Reason?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
          return NotFound(new { message = "Savings goal not found" });
        return StatusCode(403, new { message = authResult.Reason ?? "You don't have permission to access this savings goal" });
      }

      // Already earmarked? No-op if it's the same goal; otherwise reverse the old
      // earmark before applying the new one (the client needn't orchestrate this).
      if (transaction.SavingsGoalId.HasValue)
      {
        if (transaction.SavingsGoalId.Value == request.SavingsGoalId)
          return Ok(transaction);

        await ReverseEarmarkAsync(transaction);
        await _transactionService.LinkToSavingsGoalAsync(id, null, userId);
      }

      var amount = Math.Abs(transaction.Amount);
      var isIncome = IsIncomeTransaction(transaction);

      if (isIncome)
        await _savingsService.DepositFromTransactionAsync(request.SavingsGoalId, amount, id);
      else
        await _savingsService.WithdrawFromGoalAsync(request.SavingsGoalId, amount, id);

      // Link the transaction to the savings goal — compensate if this fails
      try
      {
        var updatedTransaction = await _transactionService.LinkToSavingsGoalAsync(id, request.SavingsGoalId, userId);
        return Ok(updatedTransaction);
      }
      catch
      {
        if (isIncome)
          await _savingsService.UndoDepositAsync(request.SavingsGoalId, id);
        else
          await _savingsService.UndoWithdrawalAsync(request.SavingsGoalId, id);
        throw;
      }
    }
    catch (InvalidOperationException ex)
    {
      _logger.LogWarning(ex, "Invalid operation earmarking transaction {TransactionId} to a savings goal", id);
      return BadRequest(new { message = ex.Message });
    }
    catch (ArgumentException ex)
    {
      _logger.LogWarning(ex, "Argument error earmarking transaction {TransactionId} to a savings goal", id);
      return NotFound(new { message = ex.Message });
    }
  }

  /// <summary>
  /// Remove a transaction's savings-goal earmark, reversing whichever direction was
  /// applied (deposit or withdrawal) and restoring the goal balance + budget/leftover.
  /// </summary>
  [HttpDelete("{id}/savings-withdrawal")]
  public async Task<IActionResult> RemoveFromSavingsGoal(int id)
  {
    try
    {
      var userId = GetCurrentUserId();

      var transaction = await _transactionService.GetTransactionByIdAsync(id, userId);
      if (transaction == null)
        return NotFound(new { message = "Transaction not found" });

      if (!transaction.SavingsGoalId.HasValue)
        return BadRequest(new { message = "Transaction is not linked to a savings goal" });

      var goalId = transaction.SavingsGoalId.Value;

      // Reverse the goal movement (restores balance)
      await ReverseEarmarkAsync(transaction);

      // Unlink the transaction — compensate if this fails
      try
      {
        var updatedTransaction = await _transactionService.LinkToSavingsGoalAsync(id, null, userId);
        return Ok(updatedTransaction);
      }
      catch
      {
        // Re-apply the same direction to restore the previous state
        var amount = Math.Abs(transaction.Amount);
        if (IsIncomeTransaction(transaction))
          await _savingsService.DepositFromTransactionAsync(goalId, amount, id);
        else
          await _savingsService.WithdrawFromGoalAsync(goalId, amount, id);
        throw;
      }
    }
    catch (ArgumentException ex)
    {
      _logger.LogWarning(ex, "Argument error removing savings earmark from transaction {TransactionId}", id);
      return NotFound(new { message = ex.Message });
    }
  }

  /// <summary>
  /// For a purchase paid from a goal that lives in a real account: the in-flight draws on that
  /// goal and the recent money-out rows on the account that could have moved the money.
  /// </summary>
  [HttpGet("{id}/covering-transfer/candidates")]
  public async Task<IActionResult> GetCoveringTransferCandidates(int id)
  {
    try
    {
      return Ok(await _transactionService.GetCoveringTransferCandidatesAsync(id, GetCurrentUserId()));
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
    catch (ArgumentException ex)
    {
      return NotFound(new { message = ex.Message });
    }
  }

  /// <summary>
  /// "This transfer covered that purchase": ties an earmarked draw (and optionally every other
  /// in-flight draw on the same goal) to the transfer out of the goal's account.
  /// </summary>
  [HttpPut("{id}/covering-transfer")]
  public async Task<IActionResult> LinkCoveringTransfer(int id, [FromBody] LinkCoveringTransferRequest request)
  {
    try
    {
      return Ok(await _transactionService.LinkCoveringTransferAsync(id, request, GetCurrentUserId()));
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
    catch (ArgumentException ex)
    {
      return NotFound(new { message = ex.Message });
    }
  }

  /// <summary>Puts a draw back in flight (the transfer no longer explains it).</summary>
  [HttpDelete("{id}/covering-transfer")]
  public async Task<IActionResult> UnlinkCoveringTransfer(int id)
  {
    try
    {
      return Ok(await _transactionService.UnlinkCoveringTransferAsync(id, GetCurrentUserId()));
    }
    catch (ArgumentException ex)
    {
      return NotFound(new { message = ex.Message });
    }
  }

  /// <summary>
  /// Reverses an existing savings-goal earmark for the given transaction, picking
  /// the correct direction (undo deposit for income, undo withdrawal for expense).
  /// No-op if the transaction isn't earmarked.
  /// </summary>
  private async Task ReverseEarmarkAsync(TransactionDto transaction)
  {
    if (!transaction.SavingsGoalId.HasValue)
      return;

    var goalId = transaction.SavingsGoalId.Value;
    if (IsIncomeTransaction(transaction))
      await _savingsService.UndoDepositAsync(goalId, transaction.Id);
    else
      await _savingsService.UndoWithdrawalAsync(goalId, transaction.Id);
  }

  /// <summary>
  /// Whether a transaction counts as income (credit or an income category), which
  /// determines earmark direction. Mirrors TransactionAmountExtensions.IsIncome,
  /// evaluated on the DTO the controller has in hand.
  /// </summary>
  private static bool IsIncomeTransaction(TransactionDto transaction)
  {
    return transaction.Type == TransactionType.Credit
        || (transaction.CategoryId.HasValue
            && TransactionAmountExtensions.IsIncomeCategoryId(transaction.CategoryId.Value));
  }
}
