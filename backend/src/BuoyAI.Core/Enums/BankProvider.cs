namespace BuoyAI.Core.Enums;

/// <summary>
/// Which bank-aggregation provider a connection came from. BuoyAI started Plaid-only; this
/// discriminator lets multiple aggregators coexist (#310). It is stored on
/// <see cref="Entities.PlaidConnection"/> and dispatched on by <c>IBankAggregatorRegistry</c>
/// so ongoing sync / lifecycle work routes to the right provider implementation.
///
/// <para><b>Plaid MUST stay 0</b> — every pre-existing connection defaults to it (migration 0016
/// backfills the new column to 0), so renumbering would silently re-tag live connections.</para>
/// </summary>
public enum BankProvider
{
    Plaid = 0,
    Mx = 1,
    Teller = 2,
    Finicity = 3,
    MoneyKit = 4,
    SimpleFin = 5,

    /// <summary>The BuoyAI Cloud bank feed (Plaid behind the bridge), for self-hosted servers.</summary>
    Bridge = 6,
}
