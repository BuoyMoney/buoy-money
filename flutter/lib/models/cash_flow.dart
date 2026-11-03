/// Actual money in / out / net for a period, from real transactions — credit-card payments and
/// internal transfers excluded from both sides, refunds netted. Mirrors the backend
/// CashFlowSummary (GET /transactions/cash-flow).
class CashFlow {
  final double incomeReceived;
  final double moneySpent;
  final double netCashFlow;

  /// Absolute total of transfers + card payments left out — so the UI can explain the gap
  /// between this and a naive sum of the raw rows.
  final double internalMovementExcluded;
  final double refundsNetted;

  CashFlow({
    required this.incomeReceived,
    required this.moneySpent,
    required this.netCashFlow,
    required this.internalMovementExcluded,
    required this.refundsNetted,
  });

  factory CashFlow.fromJson(Map<String, dynamic> json) {
    double d(String camel, String pascal) =>
        _toDouble(json[camel] ?? json[pascal]);
    return CashFlow(
      incomeReceived: d('incomeReceived', 'IncomeReceived'),
      moneySpent: d('moneySpent', 'MoneySpent'),
      netCashFlow: d('netCashFlow', 'NetCashFlow'),
      internalMovementExcluded: d('internalMovementExcluded', 'InternalMovementExcluded'),
      refundsNetted: d('refundsNetted', 'RefundsNetted'),
    );
  }

  static double _toDouble(dynamic v) {
    if (v == null) return 0;
    if (v is num) return v.toDouble();
    return double.tryParse(v.toString()) ?? 0;
  }
}
