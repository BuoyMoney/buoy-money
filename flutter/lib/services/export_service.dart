import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';

import 'api_client.dart';

/// A downloaded data export, ready to hand to the platform save/share helper.
class ExportFile {
  final Uint8List bytes;
  final String filename;
  final String mimeType;

  const ExportFile({
    required this.bytes,
    required this.filename,
    required this.mimeType,
  });
}

/// The two formats the backend offers on `GET /api/export`.
enum ExportFormat {
  json('json', 'application/json', 'json'),
  csv('csv', 'application/zip', 'zip');

  final String apiValue;
  final String mimeType;
  final String extension;

  const ExportFormat(this.apiValue, this.mimeType, this.extension);
}

/// Full data export (portability). Free for every plan.
class ExportService {
  /// Downloads the export as raw bytes. Throws [DioException] on failure
  /// (for example 429 when the 6-per-10-minutes limit is hit, or when an export is already running).
  Future<ExportFile> downloadExport(ExportFormat format) async {
    final Response<List<int>> response;
    try {
      response = await apiClient.dio.get<List<int>>(
        '/export',
        queryParameters: {'format': format.apiValue},
        options: Options(
          responseType: ResponseType.bytes,
          receiveTimeout: const Duration(minutes: 2),
          // A 429 or timeout here means "slow down": replaying it would burn the strict limit.
          extra: {'noTransientRetry': true},
        ),
      );
    } on DioException catch (e) {
      throw _withDecodedBody(e);
    }
    final data = response.data ?? const <int>[];
    return ExportFile(
      bytes: data is Uint8List ? data : Uint8List.fromList(data),
      filename: filenameFromDisposition(
        response.headers.value('content-disposition'),
        fallback: _fallbackName(format),
      ),
      mimeType: format.mimeType,
    );
  }

  /// With ResponseType.bytes an error body arrives as raw bytes, so the server's JSON
  /// message would never reach [apiErrorMessage]. Decode it back to JSON (or text).
  static DioException _withDecodedBody(DioException e) {
    final response = e.response;
    final body = response?.data;
    if (response == null || body is! List<int>) return e;
    try {
      final text = utf8.decode(body);
      Object? decoded;
      try {
        decoded = jsonDecode(text);
      } catch (_) {
        decoded = text;
      }
      return e.copyWith(
        response: Response<Object?>(
          requestOptions: response.requestOptions,
          data: decoded,
          statusCode: response.statusCode,
          statusMessage: response.statusMessage,
          headers: response.headers,
          isRedirect: response.isRedirect,
          redirects: response.redirects,
          extra: response.extra,
        ),
      );
    } catch (_) {
      return e;
    }
  }

  static String _fallbackName(ExportFormat format) {
    final d = DateTime.now();
    final day = '${d.year.toString().padLeft(4, '0')}-'
        '${d.month.toString().padLeft(2, '0')}-'
        '${d.day.toString().padLeft(2, '0')}';
    return 'buoy-export-$day.${format.extension}';
  }

  /// Pulls `filename` out of a Content-Disposition header, or returns [fallback].
  static String filenameFromDisposition(String? header,
      {required String fallback}) {
    if (header == null) return fallback;
    final match = RegExp(r'filename\*?=(?:UTF-8'')?"?([^";]+)"?',
            caseSensitive: false)
        .firstMatch(header);
    final name = match?.group(1)?.trim();
    if (name == null || name.isEmpty) return fallback;
    // Never trust a path from a header.
    return name.split(RegExp(r'[\\/]')).last;
  }
}

final exportService = ExportService();
