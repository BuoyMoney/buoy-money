/// One leg of a suspected transfer.
class SuspectedTransferSide {
  final int transactionId;
  final int accountId;
  final String accountName;
  final double amount;
  final DateTime date;
  final String merchantName;

  SuspectedTransferSide({
    required this.transactionId,
    required this.accountId,
    required this.accountName,
    required this.amount,
    required this.date,
    required this.merchantName,
  });

  factory SuspectedTransferSide.fromJson(Map<String, dynamic> json) {
    // Backend serializes camelCase, but models handle both casings per project convention.
    T? pick<T>(String camel, String pascal) =>
        (json[camel] ?? json[pascal]) as T?;
    final rawDate = pick<String>('date', 'Date');
    return SuspectedTransferSide(
      transactionId: pick<int>('transactionId', 'TransactionId') ?? 0,
      accountId: pick<int>('accountId', 'AccountId') ?? 0,
      accountName: pick<String>('accountName', 'AccountName') ?? '',
      amount: (pick<num>('amount', 'Amount'))?.toDouble() ?? 0.0,
      date: rawDate != null ? DateTime.parse(rawDate) : DateTime.now(),
      merchantName: pick<String>('merchantName', 'MerchantName') ?? '',
    );
  }
}

/// A transaction (or matched pair) that looks like an internal transfer but is counted as
/// spending. The user confirms (recategorize to Transfer) or dismisses (mark reviewed).
class SuspectedTransfer {
  final String confidence; // 'low' | 'medium' | 'high'
  final String reason;
  final List<SuspectedTransferSide> sides;

  SuspectedTransfer({
    required this.confidence,
    required this.reason,
    required this.sides,
  });

  /// All transaction ids across the legs — used for confirm/dismiss.
  List<int> get transactionIds => sides.map((s) => s.transactionId).toList();

  factory SuspectedTransfer.fromJson(Map<String, dynamic> json) {
    final rawSides =
        (json['sides'] ?? json['Sides']) as List<dynamic>? ?? const [];
    return SuspectedTransfer(
      confidence: (json['confidence'] ?? json['Confidence']) as String? ?? 'low',
      reason: (json['reason'] ?? json['Reason']) as String? ?? '',
      sides: rawSides
          .map((s) => SuspectedTransferSide.fromJson(s as Map<String, dynamic>))
          .toList(),
    );
  }
}
