/// Savings draw settlement (backend migration 0039).
///
/// A purchase earmarked "paid from" a goal that lives in a real account is a
/// *draw*: the envelope dropped when it was earmarked, but the money has not
/// physically left the account until a transfer does. Tying the purchase to that
/// transfer is what lets the goal tell in-flight money from drift.
library;

int? _int(Map<String, dynamic> j, String camel) =>
    (j[camel] ?? j[camel[0].toUpperCase() + camel.substring(1)]) as int?;

double _double(Map<String, dynamic> j, String camel) =>
    ((j[camel] ?? j[camel[0].toUpperCase() + camel.substring(1)]) as num?)
        ?.toDouble() ??
    0;

String _string(Map<String, dynamic> j, String camel) =>
    (j[camel] ?? j[camel[0].toUpperCase() + camel.substring(1)]) as String? ??
    '';

DateTime _date(Map<String, dynamic> j, String camel) {
  final raw = (j[camel] ?? j[camel[0].toUpperCase() + camel.substring(1)]);
  return raw is String
      ? DateTime.parse(raw)
      : DateTime.fromMillisecondsSinceEpoch(0);
}

/// The transfer-out transaction that settled a draw.
class CoveringTransfer {
  final int transactionId;
  final DateTime date;

  /// Signed as stored: negative means money left the account.
  final double amount;
  final String accountName;
  final String title;

  const CoveringTransfer({
    required this.transactionId,
    required this.date,
    required this.amount,
    required this.accountName,
    required this.title,
  });

  factory CoveringTransfer.fromJson(Map<String, dynamic> json) =>
      CoveringTransfer(
        transactionId: _int(json, 'transactionId') ?? 0,
        date: _date(json, 'date'),
        amount: _double(json, 'amount'),
        accountName: _string(json, 'accountName'),
        title: _string(json, 'title'),
      );
}

/// An earmarked purchase on a linked goal that no transfer has covered yet.
class PendingDraw {
  final int transactionId;
  final DateTime date;

  /// Positive magnitude.
  final double amount;
  final String title;

  const PendingDraw({
    required this.transactionId,
    required this.date,
    required this.amount,
    required this.title,
  });

  factory PendingDraw.fromJson(Map<String, dynamic> json) => PendingDraw(
        transactionId: _int(json, 'transactionId') ?? 0,
        date: _date(json, 'date'),
        amount: _double(json, 'amount'),
        title: _string(json, 'title'),
      );
}

/// A money-out row on the goal's account that could have moved the money.
class CoveringTransferCandidate {
  final int transactionId;
  final DateTime date;

  /// Positive magnitude.
  final double amount;
  final String title;
  final String? categoryName;

  /// Amount equals this one purchase.
  final bool matchesThis;

  /// Amount equals every pending draw on the goal added together.
  final bool matchesPendingTotal;

  const CoveringTransferCandidate({
    required this.transactionId,
    required this.date,
    required this.amount,
    required this.title,
    this.categoryName,
    this.matchesThis = false,
    this.matchesPendingTotal = false,
  });

  bool get isExactMatch => matchesThis || matchesPendingTotal;

  factory CoveringTransferCandidate.fromJson(Map<String, dynamic> json) =>
      CoveringTransferCandidate(
        transactionId: _int(json, 'transactionId') ?? 0,
        date: _date(json, 'date'),
        amount: _double(json, 'amount'),
        title: _string(json, 'title'),
        categoryName: (json['categoryName'] ?? json['CategoryName']) as String?,
        matchesThis:
            (json['matchesThis'] ?? json['MatchesThis']) as bool? ?? false,
        matchesPendingTotal: (json['matchesPendingTotal'] ??
                json['MatchesPendingTotal']) as bool? ??
            false,
      );
}

class CoveringTransferCandidates {
  final int savingsGoalId;
  final String savingsGoalName;
  final int accountId;
  final String accountName;

  /// Every in-flight draw on the goal, the current transaction included.
  final List<PendingDraw> pendingDraws;
  final double pendingTotal;
  final List<CoveringTransferCandidate> candidates;

  const CoveringTransferCandidates({
    required this.savingsGoalId,
    required this.savingsGoalName,
    required this.accountId,
    required this.accountName,
    required this.pendingDraws,
    required this.pendingTotal,
    required this.candidates,
  });

  factory CoveringTransferCandidates.fromJson(Map<String, dynamic> json) {
    List<T> list<T>(String camel, T Function(Map<String, dynamic>) f) {
      final raw = (json[camel] ??
          json[camel[0].toUpperCase() + camel.substring(1)]) as List<dynamic>?;
      return raw?.map((e) => f(e as Map<String, dynamic>)).toList() ?? <T>[];
    }

    return CoveringTransferCandidates(
      savingsGoalId: _int(json, 'savingsGoalId') ?? 0,
      savingsGoalName: _string(json, 'savingsGoalName'),
      accountId: _int(json, 'accountId') ?? 0,
      accountName: _string(json, 'accountName'),
      pendingDraws: list('pendingDraws', PendingDraw.fromJson),
      pendingTotal: _double(json, 'pendingTotal'),
      candidates: list('candidates', CoveringTransferCandidate.fromJson),
    );
  }
}
