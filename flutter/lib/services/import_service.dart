import 'dart:convert';

import 'package:dio/dio.dart';

import '../models/import_models.dart';
import 'api_client.dart';

/// Talks to `/api/import`: upload and preview, re-read with new choices, commit, undo.
///
/// Uses the raw Dio instance on purpose (like the export download): an import is a one-off,
/// network-only action and must never be queued by the offline outbox or replayed.
/// Uploads go as bytes (`MultipartFile.fromBytes`) so the same code runs on web and mobile.
class ImportService {
  static const _noReplay = {'noTransientRetry': true};

  Future<ImportPreview> preview(
    List<ImportPickedFile> files, {
    String source = 'auto',
    int? accountId,
    ImportMapping mapping = const ImportMapping(),
  }) async {
    final form = FormData();
    for (final file in files) {
      form.files.add(MapEntry(
        'files',
        MultipartFile.fromBytes(file.bytes, filename: file.name),
      ));
    }
    form.fields
      ..add(MapEntry('source', source))
      ..add(MapEntry('mapping', jsonEncode(mapping.toJson())));
    if (accountId != null) form.fields.add(MapEntry('accountId', '$accountId'));

    final response = await apiClient.dio.post<Map<String, dynamic>>(
      '/import/preview',
      data: form,
      options: Options(
        sendTimeout: const Duration(minutes: 2),
        receiveTimeout: const Duration(minutes: 2),
        extra: _noReplay,
      ),
    );
    return ImportPreview.fromJson(response.data!);
  }

  Future<ImportPreview> reparse(
    String importId, {
    String source = 'auto',
    int? accountId,
    ImportMapping mapping = const ImportMapping(),
  }) async {
    final response = await apiClient.dio.post<Map<String, dynamic>>(
      '/import/$importId/reparse',
      data: {
        'source': _sourceValue(source),
        'accountId': accountId,
        'mapping': mapping.toJson(),
      },
      options: Options(receiveTimeout: const Duration(minutes: 2), extra: _noReplay),
    );
    return ImportPreview.fromJson(response.data!);
  }

  Future<List<ImportRowPreview>> rows(
    String importId, {
    String? rowClass,
    int page = 0,
    int pageSize = 50,
  }) async {
    final response = await apiClient.dio.get<Map<String, dynamic>>(
      '/import/$importId/rows',
      queryParameters: {
        if (rowClass != null) 'cls': rowClass,
        'page': page,
        'pageSize': pageSize,
      },
    );
    final raw = (response.data?['rows'] ?? response.data?['Rows']) as List<dynamic>? ?? const [];
    return raw.map((e) => ImportRowPreview.fromJson(e as Map<String, dynamic>)).toList();
  }

  Future<ImportCommitResult> commit(String importId, ImportCommitRequest request) async {
    final response = await apiClient.dio.post<Map<String, dynamic>>(
      '/import/$importId/commit',
      data: request.toJson(),
      options: Options(receiveTimeout: const Duration(minutes: 5), extra: _noReplay),
    );
    return ImportCommitResult.fromJson(response.data!);
  }

  Future<ImportUndoResult> undo(String batchId) async {
    final response = await apiClient.dio.post<Map<String, dynamic>>(
      '/import/$batchId/undo',
      options: Options(extra: _noReplay),
    );
    return ImportUndoResult.fromJson(response.data!);
  }

  /// The API reads the source as an enum name, case-insensitively.
  static String _sourceValue(String source) => source.isEmpty ? 'auto' : source;
}

/// Shared instance. Not `final` only so tests can swap in a fake; production never reassigns it.
ImportService importService = ImportService();
