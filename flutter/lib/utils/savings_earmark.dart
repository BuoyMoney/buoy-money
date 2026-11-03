import '../models/transaction_models.dart';

/// Whether a transaction is eligible to be earmarked to a savings goal.
///
/// Mirrors the two hard rejections in `TransactionsController.ApplyToSavingsGoal`
/// so the picker never offers a goal the backend will refuse:
///
/// * **Split parents** carry no amount of their own — the split items do.
/// * **Pending holds** still change amount when they post, and the Plaid sync
///   overwrites the amount in place without reconciling the earmark, so the goal
///   balance would drift from the real charge.
///
/// Keeping this in one place means both the transaction detail picker and the AI
/// review card agree with the server instead of each guessing.
bool canEarmarkToSavings(Transaction tx) => !tx.isSplit && !tx.isPending;

/// Why [canEarmarkToSavings] is false, phrased for the user. Returns null when the
/// transaction *is* eligible.
String? savingsEarmarkBlockedReason(Transaction tx) {
  if (tx.isSplit) {
    return 'Split transactions can’t be earmarked — earmark the individual '
        'split items instead.';
  }
  if (tx.isPending) {
    return 'Still pending — once it posts you can pull it from a savings goal.';
  }
  return null;
}
