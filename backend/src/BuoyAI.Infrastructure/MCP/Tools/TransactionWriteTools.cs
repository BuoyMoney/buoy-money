using System.ComponentModel;
using System.Text.Json;
using BuoyAI.Core.DTOs.Savings;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.ValueObjects;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BuoyAI.Infrastructure.MCP.Tools;

/// <summary>
/// MCP tools for categorizing transactions.
/// </summary>
[McpServerToolType]
public sealed class TransactionWriteTools
{
    private readonly ITransactionService _transactionService;
    private readonly ISavingsService _savingsService;
    private readonly IFamilyAuthorizationService _authService;
    private readonly IUserRepository _userRepository;
    private readonly ILogger<TransactionWriteTools> _logger;

    public TransactionWriteTools(
        ITransactionService transactionService,
        ISavingsService savingsService,
        IFamilyAuthorizationService authService,
        IUserRepository userRepository,
        ILogger<TransactionWriteTools> logger)
    {
        _transactionService = transactionService;
        _savingsService = savingsService;
        _authService = authService;
        _userRepository = userRepository;
        _logger = logger;
    }

    // User-facing amounts are written in the user's primary currency (#941);
    // a bare ":C" renders "¤" in the locale-less container and pins USD.
    private async Task<string?> UserCurrencyAsync(int userId) =>
        (await _userRepository.GetByIdAsync(userId))?.Currency;

    [McpServerTool(Name = "create_manual_transaction")]
    [Description("Create a manual transaction the user entered themselves (not from a bank sync or file import). Use this when the user asks to record a purchase, payment, cash expense, or income Buoy didn't pick up. Always previews the transaction and waits for confirmation before creating it.")]
    public async Task<string> CreateManualTransactionAsync(
        [Description("User ID creating the transaction")] int userId,
        [Description("Account ID to record the transaction in (one of the user's accounts)")] int accountId,
        [Description("Signed amount. Positive = income, negative = expense. e.g. -12.50 for a $12.50 purchase")] decimal amount,
        [Description("Short description, e.g. 'Farmers market' or 'Freelance payment'")] string description,
        [Description("Merchant/vendor name (optional)")] string? merchantName = null,
        [Description("Transaction date in YYYY-MM-DD format. Defaults to today if omitted.")] string? date = null,
        [Description("Category ID (optional): 1-99 for system categories, 1000+ for custom. Defaults to Uncategorized.")] int? categoryId = null,
        [Description("User notes/memo (optional)")] string? userNotes = null,
        [Description("Set to true to actually create the transaction; false (default) returns a preview to confirm first")] bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation(
                "MCP Tool: create_manual_transaction called for user {UserId}, accountId={AccountId}, amount={Amount}, confirmed={Confirmed}",
                userId, accountId, amount, confirmed);

            if (amount == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    Error = "Amount cannot be zero.",
                    Hint = "Use a negative amount for an expense (e.g. -12.50) or a positive amount for income."
                });
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                return JsonSerializer.Serialize(new { Error = "A description is required." });
            }

            // Parse the date (default to today if omitted).
            var txDate = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(date))
            {
                if (!DateTime.TryParse(date, out var parsed))
                {
                    return JsonSerializer.Serialize(new
                    {
                        Error = $"Invalid date '{date}'.",
                        ExpectedFormat = "YYYY-MM-DD (e.g. '2026-07-09')"
                    });
                }
                txDate = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }

            var isIncome = amount > 0;
            var displayAmount = Math.Abs(amount);
            var categoryName = categoryId.HasValue
                ? (categoryId.Value >= 1000
                    ? $"Category #{categoryId.Value}"
                    : ((TransactionCategory)categoryId.Value).ToString())
                : "Uncategorized";

            if (!confirmed)
            {
                return JsonSerializer.Serialize(new
                {
                    RequiresConfirmation = true,
                    ActionType = "create_manual_transaction",
                    Preview = new
                    {
                        Direction = isIncome ? "Income" : "Expense",
                        Amount = displayAmount,
                        Description = description.Trim(),
                        MerchantName = merchantName,
                        Date = txDate.ToString("yyyy-MM-dd"),
                        Category = categoryName,
                        AccountId = accountId
                    },
                    Summary = isIncome
                        ? $"Record income of {CurrencyCodes.Format(displayAmount, await UserCurrencyAsync(userId))} '{description.Trim()}' on {txDate:MMM dd, yyyy}."
                        : $"Record expense of {CurrencyCodes.Format(displayAmount, await UserCurrencyAsync(userId))} for '{description.Trim()}' on {txDate:MMM dd, yyyy}.",
                    Note = "Confirm to create. This transaction will appear in the account and count toward the budget."
                }, new JsonSerializerOptions { WriteIndented = true });
            }

            var request = new CreateTransactionRequest
            {
                AccountId = accountId,
                Amount = amount,
                Description = description.Trim(),
                MerchantName = string.IsNullOrWhiteSpace(merchantName) ? null : merchantName.Trim(),
                Date = txDate,
                CategoryId = categoryId,
                UserNotes = string.IsNullOrWhiteSpace(userNotes) ? null : userNotes.Trim(),
            };

            var created = await _transactionService.CreateManualTransactionAsync(request, userId);

            return JsonSerializer.Serialize(new
            {
                Success = true,
                Message = $"Created {(isIncome ? "income" : "expense")} of {CurrencyCodes.Format(displayAmount, await UserCurrencyAsync(userId))} for '{created.Description}'.",
                Transaction = new
                {
                    created.Id,
                    created.Description,
                    created.MerchantName,
                    created.Amount,
                    Category = created.CategoryName,
                    Date = created.Date.ToString("yyyy-MM-dd"),
                    created.AccountName
                }
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (UnauthorizedAccessException ex)
        {
            return JsonSerializer.Serialize(new
            {
                Error = "Permission denied",
                Message = ex.Message,
                Code = "READONLY_ACCESS"
            });
        }
        catch (ArgumentException ex)
        {
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in create_manual_transaction for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = "Failed to create the transaction. Please try again." });
        }
    }

    [McpServerTool(Name = "categorize_transaction")]
    [Description("Change the category of a transaction. Use this when the user wants to recategorize a specific transaction.")]
    public async Task<string> CategorizeTransactionAsync(
        [Description("User ID making the change")] int userId,
        [Description("Transaction ID to categorize")] int transactionId,
        [Description("New category name (e.g., 'Groceries', 'DiningOut', 'Entertainment')")] string category,
        [Description("Set to true to execute the action")] bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: categorize_transaction called for user {UserId}, transactionId={TransactionId}, category={Category}, confirmed={Confirmed}", 
                userId, transactionId, category, confirmed);

            // Parse category
            if (!Enum.TryParse<TransactionCategory>(category, true, out var parsedCategory))
            {
                // Try to find a close match
                var validCategories = Enum.GetNames(typeof(TransactionCategory));
                var suggestions = validCategories
                    .Where(c => c.Contains(category, StringComparison.OrdinalIgnoreCase) || 
                               category.Contains(c, StringComparison.OrdinalIgnoreCase))
                    .Take(5)
                    .ToList();

                return JsonSerializer.Serialize(new
                {
                    Error = $"Invalid category: '{category}'",
                    ValidCategories = validCategories,
                    Suggestions = suggestions.Any() ? suggestions : null,
                    Hint = "Common categories: Groceries, DiningOut, Entertainment, Shopping, Transportation, Utilities, Healthcare, Salary, Uncategorized"
                });
            }

            // We need to get the transaction first to show current state
            // For now, build the confirmation
            var confirmation = new
            {
                RequiresConfirmation = !confirmed,
                ActionType = "categorize_transaction",
                Preview = new
                {
                    TransactionId = transactionId,
                    NewCategory = parsedCategory.ToString()
                },
                Description = $"Change transaction #{transactionId} category to '{parsedCategory}'",
                AvailableCategories = new[]
                {
                    new { Name = "Groceries", Description = "Food and household items from stores" },
                    new { Name = "DiningOut", Description = "Restaurants, cafes, takeout" },
                    new { Name = "Entertainment", Description = "Movies, games, streaming services" },
                    new { Name = "Shopping", Description = "Retail purchases, clothing, electronics" },
                    new { Name = "Transportation", Description = "Gas, parking, public transit, rideshare" },
                    new { Name = "Utilities", Description = "Electric, water, internet, phone" },
                    new { Name = "Healthcare", Description = "Doctor visits, pharmacy, medical expenses" },
                    new { Name = "Subscriptions", Description = "Recurring monthly/yearly services" },
                    new { Name = "Transfer", Description = "Money transfers between accounts" },
                    new { Name = "Uncategorized", Description = "Not yet categorized" }
                }
            };

            if (!confirmed)
            {
                return JsonSerializer.Serialize(confirmation, new JsonSerializerOptions { WriteIndented = true });
            }

            // Execute the categorization
            var request = new UpdateTransactionCategoryRequest
            {
                CategoryId = (int)parsedCategory
            };

            var updatedTransaction = await _transactionService.UpdateTransactionCategoryAsync(transactionId, request, userId);

            return JsonSerializer.Serialize(new
            {
                Success = true,
                Message = $"Updated transaction to '{parsedCategory}'!",
                Transaction = new
                {
                    updatedTransaction.Id,
                    updatedTransaction.MerchantName,
                    updatedTransaction.Description,
                    updatedTransaction.Amount,
                    Category = updatedTransaction.CategoryName,
                    Date = updatedTransaction.Date.ToString("yyyy-MM-dd")
                }
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in categorize_transaction for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "get_category_options", ReadOnly = true)]
    [Description("Get the list of available transaction categories. Use this to help the user choose a category.")]
    public Task<string> GetCategoryOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        var categories = new[]
        {
            new { Name = "Salary", Description = "Regular income from employment", IsIncome = true },
            new { Name = "Bonus", Description = "One-time bonuses or extra income", IsIncome = true },
            new { Name = "Investment", Description = "Returns from investments", IsIncome = true },
            new { Name = "OtherIncome", Description = "Miscellaneous income", IsIncome = true },
            new { Name = "Rent", Description = "Housing rent payments", IsIncome = false },
            new { Name = "Utilities", Description = "Electric, water, gas, internet", IsIncome = false },
            new { Name = "Insurance", Description = "Health, car, home insurance", IsIncome = false },
            new { Name = "Subscriptions", Description = "Recurring service subscriptions", IsIncome = false },
            new { Name = "LoanPayments", Description = "Student loans, car payments, etc.", IsIncome = false },
            new { Name = "Groceries", Description = "Food and household items", IsIncome = false },
            new { Name = "DiningOut", Description = "Restaurants and takeout", IsIncome = false },
            new { Name = "Transportation", Description = "Gas, parking, transit, rideshare", IsIncome = false },
            new { Name = "Entertainment", Description = "Movies, games, events", IsIncome = false },
            new { Name = "Shopping", Description = "Retail purchases", IsIncome = false },
            new { Name = "Healthcare", Description = "Medical expenses", IsIncome = false },
            new { Name = "Travel", Description = "Vacation and travel expenses", IsIncome = false },
            new { Name = "PersonalCare", Description = "Haircuts, gym, personal items", IsIncome = false },
            new { Name = "Education", Description = "Courses, books, learning", IsIncome = false },
            new { Name = "Gifts", Description = "Gifts given to others", IsIncome = false },
            new { Name = "CreditPayment", Description = "Credit card payments", IsIncome = false },
            new { Name = "Transfer", Description = "Transfers between accounts", IsIncome = false },
            new { Name = "Uncategorized", Description = "Not yet categorized", IsIncome = false }
        };

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            TotalCategories = categories.Length,
            IncomeCategories = categories.Where(c => c.IsIncome).Select(c => new { c.Name, c.Description }),
            ExpenseCategories = categories.Where(c => !c.IsIncome).Select(c => new { c.Name, c.Description })
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [McpServerTool(Name = "delete_transaction")]
    [Description("Delete a transaction permanently. Use this when the user wants to remove a duplicate or unwanted transaction. IMPORTANT: This is a destructive action that cannot be undone. Always confirm with the user before executing.")]
    public async Task<string> DeleteTransactionAsync(
        [Description("User ID making the deletion")] int userId,
        [Description("Transaction ID to delete")] int transactionId,
        [Description("Brief reason for deletion (e.g., 'duplicate', 'entered in error')")] string reason,
        [Description("Set to true to execute the deletion. MUST be explicitly confirmed by the user.")] bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: delete_transaction called for user {UserId}, transactionId={TransactionId}, reason={Reason}, confirmed={Confirmed}", 
                userId, transactionId, reason, confirmed);

            // Get the transaction first to show what will be deleted
            var transaction = await _transactionService.GetTransactionByIdAsync(transactionId, userId);
            if (transaction == null)
            {
                return JsonSerializer.Serialize(new
                {
                    Error = $"Transaction #{transactionId} not found or you don't have access to it",
                    Suggestion = "Please verify the transaction ID is correct"
                });
            }

            // Build confirmation details
            var confirmationDetails = new
            {
                RequiresConfirmation = !confirmed,
                ActionType = "delete_transaction",
                Warning = "⚠️ This action CANNOT be undone! The transaction will be permanently deleted.",
                Preview = new
                {
                    TransactionId = transaction.Id,
                    Description = transaction.MerchantName ?? transaction.Description,
                    Amount = transaction.Amount,
                    Date = transaction.Date.ToString("yyyy-MM-dd"),
                    Category = transaction.CategoryName,
                    Account = transaction.AccountName
                },
                Reason = reason,
                Description = $"Delete transaction: {transaction.MerchantName ?? transaction.Description} ({CurrencyCodes.Format(transaction.Amount, await UserCurrencyAsync(userId))}) from {transaction.Date:MMM dd, yyyy}",
                ConfirmationPrompt = "Please confirm you want to PERMANENTLY delete this transaction by setting confirmed=true"
            };

            if (!confirmed)
            {
                return JsonSerializer.Serialize(confirmationDetails, new JsonSerializerOptions { WriteIndented = true });
            }

            // Execute the deletion
            await _transactionService.DeleteTransactionAsync(transactionId, userId);

            return JsonSerializer.Serialize(new
            {
                Success = true,
                Message = $"Transaction #{transactionId} has been permanently deleted",
                DeletedTransaction = new
                {
                    transaction.Id,
                    Description = transaction.MerchantName ?? transaction.Description,
                    transaction.Amount,
                    Date = transaction.Date.ToString("yyyy-MM-dd")
                },
                Reason = reason
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in delete_transaction for user {UserId}, transaction {TransactionId}", userId, transactionId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "update_transaction_date")]
    [Description("Change the date of a transaction. Use this when the user says a transaction has the wrong date or wants to move it to a different day for reporting/budgeting purposes.")]
    public async Task<string> UpdateTransactionDateAsync(
        [Description("User ID making the change")] int userId,
        [Description("Transaction ID to update")] int transactionId,
        [Description("New date for the transaction in YYYY-MM-DD format (e.g., '2025-01-15')")] string newDate,
        [Description("Set to true to execute the change")] bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: update_transaction_date called for user {UserId}, transactionId={TransactionId}, newDate={NewDate}, confirmed={Confirmed}", 
                userId, transactionId, newDate, confirmed);

            // Parse the date
            if (!DateTime.TryParse(newDate, out var parsedDate))
            {
                return JsonSerializer.Serialize(new
                {
                    Error = $"Invalid date format: '{newDate}'",
                    ExpectedFormat = "YYYY-MM-DD (e.g., '2025-01-15')",
                    Examples = new[] { "2025-01-15", "2025-02-01", "2024-12-31" }
                });
            }

            // Get the transaction first to show current state
            var transaction = await _transactionService.GetTransactionByIdAsync(transactionId, userId);
            if (transaction == null)
            {
                return JsonSerializer.Serialize(new
                {
                    Error = $"Transaction #{transactionId} not found or you don't have access to it",
                    Suggestion = "Please verify the transaction ID is correct"
                });
            }

            // Build confirmation details
            var confirmationDetails = new
            {
                RequiresConfirmation = !confirmed,
                ActionType = "update_transaction_date",
                Preview = new
                {
                    TransactionId = transaction.Id,
                    Description = transaction.MerchantName ?? transaction.Description,
                    Amount = transaction.Amount,
                    CurrentDate = transaction.Date.ToString("yyyy-MM-dd"),
                    NewDate = parsedDate.ToString("yyyy-MM-dd")
                },
                Description = $"Change date of '{transaction.MerchantName ?? transaction.Description}' from {transaction.Date:MMM dd, yyyy} to {parsedDate:MMM dd, yyyy}",
                Note = "This may affect budget period calculations and reports"
            };

            if (!confirmed)
            {
                return JsonSerializer.Serialize(confirmationDetails, new JsonSerializerOptions { WriteIndented = true });
            }

            // Execute the date change
            var updatedTransaction = await _transactionService.UpdateTransactionDateAsync(transactionId, parsedDate, userId);

            return JsonSerializer.Serialize(new
            {
                Success = true,
                Message = $"Transaction date updated successfully!",
                Transaction = new
                {
                    updatedTransaction.Id,
                    Description = updatedTransaction.MerchantName ?? updatedTransaction.Description,
                    updatedTransaction.Amount,
                    PreviousDate = transaction.Date.ToString("yyyy-MM-dd"),
                    NewDate = updatedTransaction.Date.ToString("yyyy-MM-dd")
                }
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in update_transaction_date for user {UserId}, transaction {TransactionId}", userId, transactionId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "split_transaction")]
    [Description("Split one transaction into parts in different categories, for example a Walmart or Amazon charge from a receipt the user shared (groceries, household, pets). The parts must add up to the transaction amount exactly. Always previews the split and waits for confirmation before applying it. Splitting again replaces an earlier split.")]
    public async Task<string> SplitTransactionAsync(
        [Description("User ID making the change")] int userId,
        [Description("Transaction ID to split")] int transactionId,
        [Description("The parts as a JSON array, at least 2. Each part: {\"amount\": positive number, \"category\": system category name such as \"Groceries\", \"Shopping\", \"Healthcare\", \"PersonalCare\", \"Entertainment\", \"Gifts\", \"Other\", \"note\": optional short text such as the items in that part}. Example: [{\"amount\":112.40,\"category\":\"Groceries\",\"note\":\"Milk, bananas, chicken\"},{\"amount\":48.06,\"category\":\"Shopping\",\"note\":\"Paper towels, dish soap\"}]")] string parts,
        [Description("Set to true to apply the split; false (default) returns a preview to confirm first")] bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: split_transaction called for user {UserId}, transactionId={TransactionId}, confirmed={Confirmed}",
                userId, transactionId, confirmed);

            var transaction = await _transactionService.GetTransactionByIdAsync(transactionId, userId);
            if (transaction == null)
            {
                return JsonSerializer.Serialize(new
                {
                    Error = $"Transaction #{transactionId} not found or you don't have access to it",
                    Suggestion = "Find the transaction with get_transactions first"
                });
            }
            if (transaction.ParentTransactionId.HasValue)
            {
                return JsonSerializer.Serialize(new { Error = "That's one part of an already split transaction. Split the original transaction instead." });
            }
            if (transaction.SavingsGoalId.HasValue)
            {
                // Splitting would silently undo the savings goal withdrawal (SplitTransactionAsync clears it).
                return JsonSerializer.Serialize(new { Error = "This transaction is recorded as a withdrawal from a savings goal. Splitting it would remove that, so it isn't offered here. The user can remove it from the goal in the app first." });
            }

            List<SplitPartArg>? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<List<SplitPartArg>>(parts, SplitPartJson);
            }
            catch (JsonException)
            {
                parsed = null;
            }
            if (parsed == null || parsed.Count < 2)
            {
                return JsonSerializer.Serialize(new { Error = "parts must be a JSON array with at least 2 parts, each with amount and category." });
            }

            var items = new List<TransactionSplitItem>();
            foreach (var part in parsed)
            {
                if (part.Amount <= 0)
                    return JsonSerializer.Serialize(new { Error = "Every part needs an amount greater than zero." });
                if (string.IsNullOrWhiteSpace(part.Category)
                    || !Enum.TryParse<TransactionCategory>(part.Category.Replace(" ", ""), true, out var cat)
                    || !Enum.IsDefined(cat))
                {
                    return JsonSerializer.Serialize(new
                    {
                        Error = $"Unknown category: '{part.Category}'",
                        ValidCategories = Enum.GetNames(typeof(TransactionCategory))
                    });
                }
                items.Add(new TransactionSplitItem
                {
                    Amount = Math.Round(part.Amount, 2),
                    CategoryId = (int)cat,
                    Note = string.IsNullOrWhiteSpace(part.Note) ? null : part.Note.Trim(),
                });
            }

            var currency = await UserCurrencyAsync(userId);
            var total = items.Sum(i => i.Amount);
            var original = Math.Abs(transaction.Amount);
            if (total != original)
            {
                return JsonSerializer.Serialize(new
                {
                    Error = $"The parts add up to {CurrencyCodes.Format(total, currency)} but the transaction is {CurrencyCodes.Format(original, currency)}.",
                    Difference = Math.Round(original - total, 2),
                    Suggestion = "Adjust the parts (usually by spreading tax across them) so they add up exactly."
                });
            }

            var name = transaction.MerchantName ?? transaction.Description;
            var lines = items.Select(i =>
                $"{(TransactionCategory)i.CategoryId!.Value} {CurrencyCodes.Format(i.Amount, currency)}" + (i.Note != null ? $" ({i.Note})" : string.Empty));
            if (!confirmed)
            {
                return JsonSerializer.Serialize(new
                {
                    RequiresConfirmation = true,
                    ActionType = "split_transaction",
                    Preview = new
                    {
                        TransactionId = transaction.Id,
                        Description = name,
                        Amount = transaction.Amount,
                        Date = transaction.Date.ToString("yyyy-MM-dd"),
                        Parts = items.Select(i => new { Category = ((TransactionCategory)i.CategoryId!.Value).ToString(), i.Amount, i.Note })
                    },
                    Description = $"Split '{name}' ({CurrencyCodes.Format(original, currency)}, {transaction.Date:MMM dd}) into: {string.Join("; ", lines)}",
                    Note = transaction.IsSplit ? "This replaces the current split." : "It can be unsplit later from the transaction."
                }, new JsonSerializerOptions { WriteIndented = true });
            }

            var updated = await _transactionService.SplitTransactionAsync(transactionId, new SplitTransactionRequest { Splits = items }, userId);
            return JsonSerializer.Serialize(new
            {
                Success = true,
                Message = $"Split '{name}' into {items.Count} parts.",
                Transaction = new
                {
                    updated.Id,
                    Description = name,
                    updated.Amount,
                    Parts = updated.Splits?.Select(s => new { s.CategoryName, s.Amount, s.Notes })
                }
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException)
        {
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in split_transaction for user {UserId}, transaction {TransactionId}", userId, transactionId);
            return JsonSerializer.Serialize(new { Error = "The split couldn't be applied." });
        }
    }

    private static readonly JsonSerializerOptions SplitPartJson = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private sealed class SplitPartArg
    {
        public decimal Amount { get; set; }
        public string? Category { get; set; }
        public string? Note { get; set; }
    }

    [McpServerTool(Name = "earmark_transaction_to_savings_goal")]
    [Description("Earmark a transaction to a savings goal. Income (a paycheck, bonus, RSU deposit) is DEPOSITED into the goal; an expense (a purchase) is treated as a WITHDRAWAL paid from the goal. Either way the transaction is moved off the normal budget and tracked against the goal. Use this when the user wants a specific transaction to go to (or come out of) a savings goal rather than a budget category. Use get_savings_goals to find the goal ID.")]
    public async Task<string> EarmarkTransactionToSavingsGoalAsync(
        [Description("User ID making the change")] int userId,
        [Description("Transaction ID to earmark")] int transactionId,
        [Description("Savings goal ID to apply the transaction to")] int savingsGoalId,
        [Description("Set to true to execute the earmark")] bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: earmark_transaction_to_savings_goal called for user {UserId}, transactionId={TransactionId}, savingsGoalId={SavingsGoalId}, confirmed={Confirmed}",
                userId, transactionId, savingsGoalId, confirmed);

            var transaction = await _transactionService.GetTransactionByIdAsync(transactionId, userId);
            if (transaction == null)
            {
                return JsonSerializer.Serialize(new
                {
                    Error = $"Transaction #{transactionId} not found or you don't have access to it",
                    Suggestion = "Please verify the transaction ID is correct"
                });
            }

            // Split transactions carry no real amount of their own — their children do.
            if (transaction.IsSplit)
            {
                return JsonSerializer.Serialize(new
                {
                    Error = "Split transactions can't be earmarked to a savings goal. Earmark the individual split items instead."
                });
            }

            // Authorize access to the target goal (respects family read-only members).
            var authResult = await _authService.CanAccessGoalAsync(userId, savingsGoalId, ResourcePermission.Edit);
            if (!authResult.IsAuthorized)
            {
                return JsonSerializer.Serialize(new
                {
                    Error = authResult.Reason ?? "You don't have permission to modify this savings goal"
                });
            }

            // Look up the goal name for a friendly preview / message.
            var goals = await _savingsService.GetSavingsGoalsAsync(userId);
            var goal = goals.FirstOrDefault(g => g.Id == savingsGoalId);
            var goalName = goal?.Name ?? $"goal #{savingsGoalId}";

            var isIncome = IsIncomeTransaction(transaction);
            var amount = Math.Abs(transaction.Amount);
            var direction = isIncome ? "deposit" : "withdrawal";

            var confirmationDetails = new
            {
                RequiresConfirmation = !confirmed,
                ActionType = "earmark_transaction_to_savings_goal",
                Preview = new
                {
                    TransactionId = transaction.Id,
                    Description = transaction.MerchantName ?? transaction.Description,
                    Amount = amount,
                    Direction = direction,
                    SavingsGoalId = savingsGoalId,
                    SavingsGoalName = goalName
                },
                Description = isIncome
                    ? $"Deposit {CurrencyCodes.Format(amount, await UserCurrencyAsync(userId))} from '{transaction.MerchantName ?? transaction.Description}' into '{goalName}' (removed from spendable income)"
                    : $"Pay {CurrencyCodes.Format(amount, await UserCurrencyAsync(userId))} for '{transaction.MerchantName ?? transaction.Description}' from '{goalName}' (removed from the budget)"
            };

            if (!confirmed)
            {
                return JsonSerializer.Serialize(confirmationDetails, new JsonSerializerOptions { WriteIndented = true });
            }

            // If already earmarked to a DIFFERENT goal, reverse the old one first.
            if (transaction.SavingsGoalId.HasValue && transaction.SavingsGoalId.Value != savingsGoalId)
            {
                await ReverseEarmarkAsync(transaction);
                await _transactionService.LinkToSavingsGoalAsync(transactionId, null, userId);
            }
            else if (transaction.SavingsGoalId.HasValue && transaction.SavingsGoalId.Value == savingsGoalId)
            {
                return JsonSerializer.Serialize(new
                {
                    Success = true,
                    Message = $"Transaction is already earmarked to '{goalName}'.",
                    AlreadyLinked = true
                }, new JsonSerializerOptions { WriteIndented = true });
            }

            SavingsGoalDto updatedGoal;
            if (isIncome)
                updatedGoal = await _savingsService.DepositFromTransactionAsync(savingsGoalId, amount, transactionId);
            else
                updatedGoal = await _savingsService.WithdrawFromGoalAsync(savingsGoalId, amount, transactionId);

            // Link the transaction — compensate if this fails.
            try
            {
                await _transactionService.LinkToSavingsGoalAsync(transactionId, savingsGoalId, userId);
            }
            catch
            {
                if (isIncome)
                    await _savingsService.UndoDepositAsync(savingsGoalId, transactionId);
                else
                    await _savingsService.UndoWithdrawalAsync(savingsGoalId, transactionId);
                throw;
            }

            return JsonSerializer.Serialize(new
            {
                Success = true,
                Message = isIncome
                    ? $"Deposited {CurrencyCodes.Format(amount, await UserCurrencyAsync(userId))} into '{goalName}'."
                    : $"Paid {CurrencyCodes.Format(amount, await UserCurrencyAsync(userId))} from '{goalName}'.",
                Direction = direction,
                SavingsGoal = new
                {
                    updatedGoal.Id,
                    updatedGoal.Name,
                    updatedGoal.CurrentAmount,
                    updatedGoal.TargetAmount,
                    updatedGoal.ProgressPercentage,
                    updatedGoal.IsCompleted
                }
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in earmark_transaction_to_savings_goal for user {UserId}, transaction {TransactionId}", userId, transactionId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "remove_transaction_savings_earmark")]
    [Description("Remove a transaction's savings-goal earmark, returning it to the normal budget. Reverses the original deposit or withdrawal and restores the goal balance and the budget/income pool. Use this when the user says a transaction should not have gone to (or come out of) a savings goal.")]
    public async Task<string> RemoveTransactionSavingsEarmarkAsync(
        [Description("User ID making the change")] int userId,
        [Description("Transaction ID to un-earmark")] int transactionId,
        [Description("Set to true to execute the removal")] bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: remove_transaction_savings_earmark called for user {UserId}, transactionId={TransactionId}, confirmed={Confirmed}",
                userId, transactionId, confirmed);

            var transaction = await _transactionService.GetTransactionByIdAsync(transactionId, userId);
            if (transaction == null)
            {
                return JsonSerializer.Serialize(new
                {
                    Error = $"Transaction #{transactionId} not found or you don't have access to it",
                    Suggestion = "Please verify the transaction ID is correct"
                });
            }

            if (!transaction.SavingsGoalId.HasValue)
            {
                return JsonSerializer.Serialize(new
                {
                    Error = "This transaction isn't earmarked to a savings goal, so there's nothing to remove."
                });
            }

            var amount = Math.Abs(transaction.Amount);
            var goalName = transaction.SavingsGoalName ?? $"goal #{transaction.SavingsGoalId.Value}";

            var confirmationDetails = new
            {
                RequiresConfirmation = !confirmed,
                ActionType = "remove_transaction_savings_earmark",
                Preview = new
                {
                    TransactionId = transaction.Id,
                    Description = transaction.MerchantName ?? transaction.Description,
                    Amount = amount,
                    SavingsGoalName = goalName
                },
                Description = $"Return {CurrencyCodes.Format(amount, await UserCurrencyAsync(userId))} '{transaction.MerchantName ?? transaction.Description}' from '{goalName}' back to the budget"
            };

            if (!confirmed)
            {
                return JsonSerializer.Serialize(confirmationDetails, new JsonSerializerOptions { WriteIndented = true });
            }

            var goalId = transaction.SavingsGoalId.Value;

            // Reverse the goal movement, then unlink — compensate if the unlink fails.
            await ReverseEarmarkAsync(transaction);
            try
            {
                await _transactionService.LinkToSavingsGoalAsync(transactionId, null, userId);
            }
            catch
            {
                if (IsIncomeTransaction(transaction))
                    await _savingsService.DepositFromTransactionAsync(goalId, amount, transactionId);
                else
                    await _savingsService.WithdrawFromGoalAsync(goalId, amount, transactionId);
                throw;
            }

            return JsonSerializer.Serialize(new
            {
                Success = true,
                Message = $"Returned {CurrencyCodes.Format(amount, await UserCurrencyAsync(userId))} to the budget and removed it from '{goalName}'."
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in remove_transaction_savings_earmark for user {UserId}, transaction {TransactionId}", userId, transactionId);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Reverses an existing savings-goal earmark, picking the correct direction
    /// (undo deposit for income, undo withdrawal for expense). No-op if unlinked.
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
    /// determines earmark direction. Mirrors TransactionAmountExtensions.IsIncome.
    /// </summary>
    private static bool IsIncomeTransaction(TransactionDto transaction)
    {
        return transaction.Type == TransactionType.Credit
            || (transaction.CategoryId.HasValue
                && TransactionAmountExtensions.IsIncomeCategoryId(transaction.CategoryId.Value));
    }
}
