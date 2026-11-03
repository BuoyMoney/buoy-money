/// Receipt scanning: receipts the user sent in (a shared screenshot, a photo or a PDF) and the
/// splits offered for them. The backend returns camelCase; PascalCase is accepted too.
library;

dynamic _v(Map<String, dynamic> json, String key) {
  if (json.containsKey(key)) return json[key];
  final pascal = key[0].toUpperCase() + key.substring(1);
  return json[pascal];
}

double _d(dynamic v) => v is num ? v.toDouble() : double.tryParse('$v') ?? 0;
DateTime? _date(dynamic v) => v == null ? null : DateTime.tryParse('$v');

/// Why receipt scanning can't be used yet.
enum ReceiptBlocker { none, premiumRequired, aiConsentRequired, dailyLimit, unavailable }

extension ReceiptBlockerX on ReceiptBlocker {
  static ReceiptBlocker fromString(String? s) {
    switch (s) {
      case 'premium_required':
        return ReceiptBlocker.premiumRequired;
      case 'ai_consent_required':
        return ReceiptBlocker.aiConsentRequired;
      case 'daily_limit':
        return ReceiptBlocker.dailyLimit;
      case 'unavailable':
        return ReceiptBlocker.unavailable;
      default:
        return ReceiptBlocker.none;
    }
  }

  static ReceiptBlocker fromInt(int i) =>
      i >= 0 && i < ReceiptBlocker.values.length ? ReceiptBlocker.values[i] : ReceiptBlocker.none;

  String get displayName => switch (this) {
        ReceiptBlocker.none => 'none',
        ReceiptBlocker.premiumRequired => 'premium_required',
        ReceiptBlocker.aiConsentRequired => 'ai_consent_required',
        ReceiptBlocker.dailyLimit => 'daily_limit',
        ReceiptBlocker.unavailable => 'unavailable',
      };
}

/// Where a receipt is in its life (ReceiptScanStatus on the server).
enum ReceiptStatus { unmatched, ready, applied, dismissed, notAReceipt, expired, unknown }

extension ReceiptStatusX on ReceiptStatus {
  static ReceiptStatus fromString(String? s) {
    switch (s) {
      case 'unmatched':
        return ReceiptStatus.unmatched;
      case 'ready':
        return ReceiptStatus.ready;
      case 'applied':
        return ReceiptStatus.applied;
      case 'dismissed':
        return ReceiptStatus.dismissed;
      case 'not_a_receipt':
        return ReceiptStatus.notAReceipt;
      case 'expired':
        return ReceiptStatus.expired;
      default:
        return ReceiptStatus.unknown;
    }
  }

  static ReceiptStatus fromInt(int i) =>
      i >= 0 && i < ReceiptStatus.values.length ? ReceiptStatus.values[i] : ReceiptStatus.unknown;

  String get displayName => name;
}

class ReceiptScanStatusInfo {
  final ReceiptBlocker blocker;
  final int readyCount;
  final List<String> stores;

  const ReceiptScanStatusInfo({this.blocker = ReceiptBlocker.none, this.readyCount = 0, this.stores = const []});

  factory ReceiptScanStatusInfo.fromJson(Map<String, dynamic> json) => ReceiptScanStatusInfo(
        blocker: ReceiptBlockerX.fromString(_v(json, 'blocker') as String?),
        readyCount: (_v(json, 'readyCount') as num?)?.toInt() ?? 0,
        stores: ((_v(json, 'stores') as List?) ?? const []).map((s) => '$s').toList(),
      );
}

class ReceiptSplitPart {
  final int categoryId;
  final String? categoryName;
  final double amount;
  final List<String> items;

  const ReceiptSplitPart({required this.categoryId, this.categoryName, required this.amount, this.items = const []});

  factory ReceiptSplitPart.fromJson(Map<String, dynamic> json) => ReceiptSplitPart(
        categoryId: (_v(json, 'categoryId') as num?)?.toInt() ?? 0,
        categoryName: _v(json, 'categoryName') as String?,
        amount: _d(_v(json, 'amount')),
        items: ((_v(json, 'items') as List?) ?? const []).map((s) => '$s').toList(),
      );
}

class ReceiptCharge {
  final int transactionId;
  final double amount;
  final DateTime? date;
  final List<ReceiptSplitPart> splits;

  const ReceiptCharge({required this.transactionId, required this.amount, this.date, this.splits = const []});

  factory ReceiptCharge.fromJson(Map<String, dynamic> json) => ReceiptCharge(
        transactionId: (_v(json, 'transactionId') as num?)?.toInt() ?? 0,
        amount: _d(_v(json, 'amount')),
        date: _date(_v(json, 'date')),
        splits: ((_v(json, 'splits') as List?) ?? const [])
            .map((s) => ReceiptSplitPart.fromJson(Map<String, dynamic>.from(s as Map)))
            .toList(),
      );
}

class ReceiptScan {
  final int id;
  final ReceiptStatus status;
  final String? retailer;
  final double? total;
  final DateTime? receivedAt;
  final String? message;
  final int itemCount;
  final List<ReceiptCharge> charges;

  const ReceiptScan({
    required this.id,
    required this.status,
    this.retailer,
    this.total,
    this.receivedAt,
    this.message,
    this.itemCount = 0,
    this.charges = const [],
  });

  /// Distinct categories across every matched charge.
  int get categoryCount => charges.expand((c) => c.splits).map((s) => s.categoryId).toSet().length;

  double get chargedTotal => charges.fold(0.0, (sum, c) => sum + c.amount);

  factory ReceiptScan.fromJson(Map<String, dynamic> json) {
    final proposal = _v(json, 'proposal');
    final charges = proposal is Map
        ? ((Map<String, dynamic>.from(proposal))['charges'] as List? ?? const [])
            .map((c) => ReceiptCharge.fromJson(Map<String, dynamic>.from(c as Map)))
            .toList()
        : const <ReceiptCharge>[];
    final total = _v(json, 'total');
    return ReceiptScan(
      id: (_v(json, 'id') as num?)?.toInt() ?? 0,
      status: ReceiptStatusX.fromString(_v(json, 'status') as String?),
      retailer: _v(json, 'retailer') as String?,
      total: total == null ? null : _d(total),
      receivedAt: _date(_v(json, 'receivedAt')),
      message: _v(json, 'message') as String?,
      itemCount: (_v(json, 'itemCount') as num?)?.toInt() ?? 0,
      charges: charges,
    );
  }
}

/// A screenshot, photo or PDF about to be sent.
class ReceiptUpload {
  final List<int> bytes;
  final String name;

  const ReceiptUpload(this.bytes, this.name);

  bool get isPdf => name.toLowerCase().endsWith('.pdf') || (bytes.length > 4 && bytes[0] == 0x25 && bytes[1] == 0x50);
}
