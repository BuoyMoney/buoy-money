namespace BuoyAI.Infrastructure.Services.SimpleFin;

/// <summary>One transaction as the SimpleFIN bridge reports it. Amount is positive for money in.</summary>
public sealed record SimpleFinTransaction(
    string Id,
    DateTimeOffset? Posted,
    decimal Amount,
    string Description,
    DateTimeOffset? TransactedAt,
    bool Pending);

/// <summary>One account from the bridge, with the transactions that fell in the asked window.</summary>
public sealed record SimpleFinAccount(
    string Id,
    string Name,
    string OrgName,
    string Currency,
    decimal? Balance,
    decimal? AvailableBalance,
    IReadOnlyList<SimpleFinTransaction> Transactions);

/// <summary>
/// What <c>/accounts</c> returned. <see cref="Messages"/> are the bridge's own error texts, already
/// cleaned so they are safe to show. <see cref="AuthProblem"/> means the bridge says the user has to
/// sign in or re-authorise something; the accounts may still hold data from the rest.
/// </summary>
public sealed record SimpleFinAccountSet(
    IReadOnlyList<SimpleFinAccount> Accounts,
    IReadOnlyList<string> Messages,
    bool AuthProblem);

/// <summary>A SimpleFIN call failed in a way the user can be told about. The message is safe to show.</summary>
public class SimpleFinException : Exception
{
    public SimpleFinException(string message) : base(message) { }
}

/// <summary>
/// The bridge refused our credentials (403/401), or reported a sign-in problem. The user has to
/// make a new setup token. The message starts with <see cref="Marker"/> so the sync failure
/// handler can tell this from a passing error.
/// </summary>
public sealed class SimpleFinAuthException : SimpleFinException
{
    public const string Marker = "SFIN_AUTH";

    public SimpleFinAuthException(string message) : base($"{Marker}: {message}") { }
}
