
import 'package:dio/dio.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:buoy_ai/services/api_client.dart';
import 'package:buoy_ai/services/export_service.dart';
import 'package:buoy_ai/utils/api_error.dart';

/// Captures the single request the export service makes and answers with bytes.
class _CaptureAdapter implements HttpClientAdapter {
  RequestOptions? last;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    last = options;
    return ResponseBody.fromBytes(
      Uint8List.fromList([80, 75, 3, 4]),
      200,
      headers: {
        Headers.contentTypeHeader: ['application/zip'],
        'content-disposition': ['attachment; filename=buoy-export-2026-10-12.zip'],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    const channel =
        MethodChannel('plugins.it_nomads.com/flutter_secure_storage');
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(channel, (call) async => null);
  });

  test('downloadExport requests /export?format=csv as bytes and reads the filename',
      () async {
    final adapter = _CaptureAdapter();
    apiClient.dio.httpClientAdapter = adapter;

    final file = await ExportService().downloadExport(ExportFormat.csv);

    expect(adapter.last!.path, contains('/export'));
    expect(adapter.last!.method, 'GET');
    expect(adapter.last!.queryParameters['format'], 'csv');
    expect(adapter.last!.responseType, ResponseType.bytes);
    expect(file.bytes, [80, 75, 3, 4]);
    expect(file.filename, 'buoy-export-2026-10-12.zip');
    expect(file.mimeType, 'application/zip');
  });

  test('json format maps to application/json', () async {
    apiClient.dio.httpClientAdapter = _CaptureAdapter();

    final file = await ExportService().downloadExport(ExportFormat.json);

    expect(file.mimeType, 'application/json');
  });

  test('a 429 makes exactly one request (no transient replay)', () async {
    final adapter = _StatusAdapter(429, '{"message":"Slow down"}');
    apiClient.dio.httpClientAdapter = adapter;

    DioException? caught;
    try {
      await ExportService().downloadExport(ExportFormat.json);
    } on DioException catch (e) {
      caught = e;
    }

    expect(caught, isNotNull);
    expect(caught!.response!.statusCode, 429);
    expect(adapter.calls, 1);
  });

  test('a 500 error body is decoded from bytes so the message survives',
      () async {
    apiClient.dio.httpClientAdapter =
        _StatusAdapter(500, '{"message":"Export exploded"}');

    DioException? caught;
    try {
      await ExportService().downloadExport(ExportFormat.csv);
    } on DioException catch (e) {
      caught = e;
    }

    expect(caught!.response!.data, {'message': 'Export exploded'});
    expect(apiErrorMessage(caught, fallback: 'generic'), 'Export exploded');
  });
}

/// Answers every request with [status] and a JSON body, counting the requests.
class _StatusAdapter implements HttpClientAdapter {
  final int status;
  final String body;
  int calls = 0;

  _StatusAdapter(this.status, this.body);

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    calls++;
    return ResponseBody.fromString(body, status, headers: {
      Headers.contentTypeHeader: ['application/json'],
    });
  }

  @override
  void close({bool force = false}) {}
}
