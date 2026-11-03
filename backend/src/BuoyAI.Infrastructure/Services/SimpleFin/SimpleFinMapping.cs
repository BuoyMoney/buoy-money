using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Utilities;

namespace BuoyAI.Infrastructure.Services.SimpleFin;

/// <summary>
/// How SimpleFIN's data becomes BuoyAI's: the ids we store, the account type we guess (the bridge
/// sends none), and the transaction record handed to ingest.
/// </summary>
public static class SimpleFinMapping
{
    /// <summary>
    /// Every SimpleFIN id we store starts with this. Accounts and transactions of every provider
    /// share one unique column (<c>PlaidAccountId</c>, <c>PlaidTransactionId</c>), so the prefix is
    /// what keeps a SimpleFIN id from ever equalling a Plaid one. Past the prefix, an account id
    /// carries the connection row's id and a transaction id carries the connection and account too,
    /// so two connections (or two accounts) that reuse the same raw ids never collide.
    /// </summary>
    public const string IdPrefix = "sfin:";

    // The columns hold 100 characters.
    private const int MaxIdLength = 100;

    public static string AccountId(int connectionId, string rawAccountId) =>
        Namespaced(connectionId.ToString(), rawAccountId);

    public static string TransactionId(int connectionId, string rawAccountId, string rawTransactionId) =>
        Namespaced(connectionId.ToString(), rawAccountId, rawTransactionId);

    /// <summary>The connection's own id: a fingerprint of the access URL, which is itself never stored in the clear.</summary>
    public static string ItemId(string accessUrl)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(accessUrl));
        return IdPrefix + Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    private static string Namespaced(params string[] parts)
    {
        // Escape the delimiter (and the escape character) so ids that contain ':' cannot collide.
        var trimmed = parts.Select(p => p.Trim()
            .Replace("%", "%25").Replace(":", "%3A").Replace("\n", "%0A").Replace("\r", "%0D")).ToArray();
        var id = IdPrefix + string.Join(":", trimmed);
        if (id.Length <= MaxIdLength)
        {
            return id;
        }

        // An unusually long id: keep it unique by hashing it instead of cutting it. The parts are
        // joined with a separator that cannot be confused with a different split of the same text.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", trimmed)));
        return IdPrefix + "h:" + Convert.ToHexString(hash)[..40].ToLowerInvariant();
    }

    private static readonly Regex OutOfScope = new(
        @"\b(401k|403b|457b?|ira|roth|brokerage|investment|retirement|mortgage|loan|heloc|annuity)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Debit = new(@"\bdebit\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Credit = new(
        @"\b(credit|visa|mastercard|master card|amex|american express|discover|card)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Savings = new(
        @"\b(savings?|money market)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// SimpleFIN sends a name and no account type, so the type is a guess from the name. Retirement,
    /// brokerage and loan names land outside the budget scope and are dropped; names that read as
    /// a credit card or savings account get that type; everything else is a checking account.
    /// </summary>
    public static AccountType GuessAccountType(string accountName)
    {
        if (OutOfScope.IsMatch(accountName))
        {
            return AccountType.Other;
        }
        if (Debit.IsMatch(accountName))
        {
            return AccountType.Checking;
        }
        if (Credit.IsMatch(accountName))
        {
            return AccountType.CreditCard;
        }
        return Savings.IsMatch(accountName) ? AccountType.Savings : AccountType.Checking;
    }

    /// <summary>
    /// The balance as BuoyAI keeps it. SimpleFIN shows money owed on a card as a negative balance;
    /// Plaid, and so the rest of BuoyAI, shows it as a positive amount owed.
    /// </summary>
    public static decimal StoredBalance(AccountType type, decimal balance) =>
        type == AccountType.CreditCard ? -balance : balance;

    public static string CurrencyOrDefault(string currency) =>
        currency.Length == 3 && currency.All(char.IsAsciiLetter) ? currency.ToUpperInvariant() : "USD";

    public static IncomingTransaction ToIncoming(int connectionId, SimpleFinAccount account, SimpleFinTransaction transaction)
    {
        var description = transaction.Description.Trim();
        return new IncomingTransaction
        {
            Provider = BankProvider.SimpleFin,
            ExternalTransactionId = TransactionId(connectionId, account.Id, transaction.Id),
            ExternalAccountId = AccountId(connectionId, account.Id),
            IsPending = false,

            // SimpleFIN already uses BuoyAI's sign: money in is positive, money out is negative.
            Amount = transaction.Amount,
            Direction = transaction.Amount < 0 ? TransactionType.Debit : TransactionType.Credit,

            Date = transaction.Posted is { } posted ? DateOnly.FromDateTime(posted.UtcDateTime) : null,
            AuthorizedDate = transaction.TransactedAt is { } at ? DateOnly.FromDateTime(at.UtcDateTime) : null,

            Description = SafeText.Truncate(description.Length > 0 ? description : "Transaction", 500),
            OriginalDescription = description.Length > 0 ? SafeText.Truncate(description, 500) : null,
            IsoCurrencyCode = CurrencyOrDefault(account.Currency),

            // The bridge gives no category: rules and the AI queue decide, and the user reviews.
            CategoryId = SystemCategoryIds.Uncategorized,
            NeedsReview = true,
            CategorySource = "SimpleFin",
        };
    }
}
