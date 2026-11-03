import 'api_client.dart';

/// The answer of `POST /api/admin/broadcast`, for a dry run or a real send.
class BroadcastResult {
  final bool dryRun;
  final String status;
  final int recipientCount;
  final int skippedCount;
  final String? previewHtml;

  const BroadcastResult({
    required this.dryRun,
    required this.status,
    required this.recipientCount,
    required this.skippedCount,
    this.previewHtml,
  });

  factory BroadcastResult.fromJson(Map<String, dynamic> json) =>
      BroadcastResult(
        dryRun: json['dryRun'] == true,
        status: (json['status'] ?? '').toString(),
        recipientCount: (json['recipientCount'] as num?)?.toInt() ?? 0,
        skippedCount: (json['skippedCount'] as num?)?.toInt() ?? 0,
        previewHtml: json['previewHtml'] as String?,
      );
}

/// Admin broadcast email (hosted edition only; the server answers 404 on a self-hosted one).
class BroadcastService {
  /// Runs the dry run: counts recipients and sends nothing.
  Future<BroadcastResult> preview({
    required String subject,
    required String bodyMarkdown,
    required String audience,
    required String category,
  }) =>
      _post(subject, bodyMarkdown, audience, category, dryRun: true);

  /// Sends for real. [confirmRecipientCount] must equal the count the dry run returned.
  /// [idempotencyKey] makes a retry of the same send a no-op on the server.
  Future<BroadcastResult> send({
    required String subject,
    required String bodyMarkdown,
    required String audience,
    required String category,
    required int confirmRecipientCount,
    String? idempotencyKey,
  }) =>
      _post(subject, bodyMarkdown, audience, category,
          dryRun: false,
          confirm: confirmRecipientCount,
          idempotencyKey: idempotencyKey);

  Future<BroadcastResult> _post(
    String subject,
    String bodyMarkdown,
    String audience,
    String category, {
    required bool dryRun,
    int? confirm,
    String? idempotencyKey,
  }) async {
    final json = await apiClient.post<Map<String, dynamic>>(
      '/admin/broadcast',
      data: {
        'subject': subject,
        'bodyMarkdown': bodyMarkdown,
        'audience': audience,
        'category': category,
        'dryRun': dryRun,
        if (confirm != null) 'confirmRecipientCount': confirm,
        if (idempotencyKey != null) 'idempotencyKey': idempotencyKey,
      },
    );
    return BroadcastResult.fromJson(json);
  }
}

final broadcastService = BroadcastService();
