import 'package:dio/dio.dart';
import '../models/receipt_models.dart';
import 'api_client.dart';

/// Thrown when a receipt can't be sent or read; [message] is written for the user and
/// [blocker] says when the account is the reason (Premium, AI consent, daily limit).
class ReceiptScanException implements Exception {
  final String? message;
  final ReceiptBlocker blocker;

  const ReceiptScanException(this.message, {this.blocker = ReceiptBlocker.none});

  @override
  String toString() => message ?? 'Receipt scan failed';
}

/// Receipt scanning (backend: ReceiptsController). The user shares a screenshot of an order page,
/// a photo of a paper receipt or a PDF; the server reads it while the sheet waits and returns the
/// split it offers for the matching bank charge. Files are never stored on the server.
class ReceiptsService {
  ReceiptsService({ApiClient? client}) : _client = client ?? apiClient;

  final ApiClient _client;

  /// Shown when the upload is refused for size before it reaches the API (a proxy's 413 has no JSON body).
  static const tooLargeMessage = 'Those files are too big to send. Try fewer screenshots.';

  Future<ReceiptScanStatusInfo> status() async =>
      ReceiptScanStatusInfo.fromJson(await _client.get<Map<String, dynamic>>('/receipts/status'));

  /// One receipt: one to four files showing one order. [transactionId] is the charge it was
  /// scanned from, if any.
  Future<ReceiptScan> scan(List<ReceiptUpload> files, {int? transactionId}) async {
    final form = FormData();
    for (final f in files) {
      form.files.add(MapEntry('files', MultipartFile.fromBytes(f.bytes, filename: f.name)));
    }
    if (transactionId != null) form.fields.add(MapEntry('transactionId', '$transactionId'));
    try {
      final response = await _client.dio.post(
        '/receipts',
        data: form,
        options: Options(receiveTimeout: const Duration(seconds: 90), sendTimeout: const Duration(seconds: 90)),
      );
      return ReceiptScan.fromJson(Map<String, dynamic>.from(response.data as Map));
    } on DioException catch (e) {
      if (e.response?.statusCode == 413) {
        throw const ReceiptScanException(tooLargeMessage);
      }
      final data = e.response?.data;
      final body = data is Map ? Map<String, dynamic>.from(data) : const <String, dynamic>{};
      throw ReceiptScanException(
        body['message'] as String?,
        blocker: ReceiptBlockerX.fromString(body['reason'] as String?),
      );
    }
  }

  Future<List<ReceiptScan>> list() async {
    final rows = await _client.get<List<dynamic>>('/receipts');
    return rows.map((r) => ReceiptScan.fromJson(Map<String, dynamic>.from(r as Map))).toList();
  }

  /// The receipt that matched a transaction, or null.
  Future<ReceiptScan?> forTransaction(int transactionId) async {
    final row = await _client.get<dynamic>('/receipts/for-transaction/$transactionId');
    return row is Map ? ReceiptScan.fromJson(Map<String, dynamic>.from(row)) : null;
  }

  Future<ReceiptScan> apply(int id) async =>
      ReceiptScan.fromJson(await _client.post<Map<String, dynamic>>('/receipts/$id/apply'));

  Future<ReceiptScan> dismiss(int id) async =>
      ReceiptScan.fromJson(await _client.post<Map<String, dynamic>>('/receipts/$id/dismiss'));
}

final receiptsService = ReceiptsService();
