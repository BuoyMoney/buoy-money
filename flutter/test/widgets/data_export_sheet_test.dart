import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:google_fonts/google_fonts.dart';

import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/services/export_service.dart';
import 'package:buoy_ai/widgets/data_export_sheet.dart';

Widget _host(Widget sheet) => MaterialApp(
      localizationsDelegates: const [
        AppLocalizations.delegate,
        GlobalMaterialLocalizations.delegate,
        GlobalWidgetsLocalizations.delegate,
        GlobalCupertinoLocalizations.delegate,
      ],
      supportedLocales: AppLocalizations.supportedLocales,
      home: Scaffold(body: sheet),
    );

DioException _dioError(int status, {Object? data}) => DioException(
      requestOptions: RequestOptions(path: '/export'),
      response: Response(
        requestOptions: RequestOptions(path: '/export'),
        statusCode: status,
        data: data,
      ),
      type: DioExceptionType.badResponse,
    );

void main() {
  setUpAll(() {
    GoogleFonts.config.allowRuntimeFetching = false;
  });

  testWidgets('shows both formats and the what-is-included description', (
    tester,
  ) async {
    await tester.pumpWidget(_host(const DataExportSheet()));

    expect(find.text('Download my data'), findsOneWidget);
    expect(find.text('JSON file'), findsOneWidget);
    expect(find.text('CSV spreadsheets (zip)'), findsOneWidget);
    expect(find.textContaining('never included'), findsOneWidget);
  });

  testWidgets('downloads JSON by default and hands bytes to the saver', (
    tester,
  ) async {
    ExportFormat? requested;
    String? savedName;
    String? savedMime;
    await tester.pumpWidget(
      _host(
        DataExportSheet(
          downloader: (format) async {
            requested = format;
            return ExportFile(
              bytes: Uint8List.fromList([1, 2, 3]),
              filename: 'buoy-export-2026-10-12.json',
              mimeType: 'application/json',
            );
          },
          saver: (
            bytes, {
            required filename,
            required mimeType,
            sharePositionOrigin,
          }) async {
            savedName = filename;
            savedMime = mimeType;
            expect(bytes, [1, 2, 3]);
          },
        ),
      ),
    );

    await tester.tap(find.byKey(const ValueKey('export-download-button')));
    await tester.pump();
    await tester.pump();

    expect(requested, ExportFormat.json);
    expect(savedName, 'buoy-export-2026-10-12.json');
    expect(savedMime, 'application/json');
    expect(find.text('Your data export is ready'), findsOneWidget);
    await tester.pump(const Duration(seconds: 5));
  });

  testWidgets('choosing CSV requests the csv format', (tester) async {
    ExportFormat? requested;
    await tester.pumpWidget(
      _host(
        DataExportSheet(
          downloader: (format) async {
            requested = format;
            return ExportFile(
              bytes: Uint8List(0),
              filename: 'buoy-export.zip',
              mimeType: 'application/zip',
            );
          },
          saver: (
            bytes, {
            required filename,
            required mimeType,
            sharePositionOrigin,
          }) async {},
        ),
      ),
    );

    await tester.tap(find.byKey(const ValueKey('export-format-csv')));
    await tester.pump();
    await tester.tap(find.byKey(const ValueKey('export-download-button')));
    await tester.pump();
    await tester.pump();

    expect(requested, ExportFormat.csv);
    await tester.pump(const Duration(seconds: 5));
  });

  testWidgets('shows a loading state and disables the button while fetching', (
    tester,
  ) async {
    final completer = Completer<ExportFile>();
    await tester.pumpWidget(
      _host(
        DataExportSheet(
          downloader: (format) => completer.future,
          saver: (
            bytes, {
            required filename,
            required mimeType,
            sharePositionOrigin,
          }) async {},
        ),
      ),
    );

    await tester.tap(find.byKey(const ValueKey('export-download-button')));
    await tester.pump();

    expect(find.text('Preparing your export...'), findsOneWidget);
    expect(find.byType(CircularProgressIndicator), findsOneWidget);
    final button = tester.widget<FilledButton>(
      find.byKey(const ValueKey('export-download-button')),
    );
    expect(button.onPressed, isNull);

    completer.complete(
      ExportFile(
        bytes: Uint8List(0),
        filename: 'a.json',
        mimeType: 'application/json',
      ),
    );
    await tester.pump();
    await tester.pump();
    await tester.pump(const Duration(seconds: 5));
  });

  testWidgets('shows an error and allows a retry when the download fails', (
    tester,
  ) async {
    var calls = 0;
    await tester.pumpWidget(
      _host(
        DataExportSheet(
          downloader: (format) async {
            calls++;
            throw _dioError(500);
          },
          saver: (
            bytes, {
            required filename,
            required mimeType,
            sharePositionOrigin,
          }) async {},
        ),
      ),
    );

    await tester.tap(find.byKey(const ValueKey('export-download-button')));
    await tester.pump();
    await tester.pump();

    expect(find.byKey(const ValueKey('export-error')), findsOneWidget);
    expect(
      find.textContaining('Couldn\'t prepare your export'),
      findsOneWidget,
    );

    await tester.tap(find.byKey(const ValueKey('export-download-button')));
    await tester.pump();
    await tester.pump();
    expect(calls, 2);
  });

  testWidgets('429 shows the rate-limit message', (tester) async {
    await tester.pumpWidget(
      _host(
        DataExportSheet(
          downloader: (format) async => throw _dioError(429),
          saver: (
            bytes, {
            required filename,
            required mimeType,
            sharePositionOrigin,
          }) async {},
        ),
      ),
    );

    await tester.tap(find.byKey(const ValueKey('export-download-button')));
    await tester.pump();
    await tester.pump();

    expect(find.textContaining('wait about 10 minutes'), findsOneWidget);
  });

  testWidgets('429 from the concurrency gate shows the one-minute message', (
    tester,
  ) async {
    await tester.pumpWidget(
      _host(
        DataExportSheet(
          downloader: (format) async => throw _dioError(
            429,
            data: {
              'message': 'An export is already running.',
              'code': 'export_in_progress',
            },
          ),
          saver: (
            bytes, {
            required filename,
            required mimeType,
            sharePositionOrigin,
          }) async {},
        ),
      ),
    );

    await tester.tap(find.byKey(const ValueKey('export-download-button')));
    await tester.pump();
    await tester.pump();

    expect(find.textContaining('already running'), findsOneWidget);
    expect(find.textContaining('wait about 10 minutes'), findsNothing);
  });

  testWidgets('format options are keyboard focusable and Enter selects CSV', (
    tester,
  ) async {
    ExportFormat? requested;
    await tester.pumpWidget(
      _host(
        DataExportSheet(
          downloader: (format) async {
            requested = format;
            return ExportFile(
              bytes: Uint8List(1),
              filename: 'x.zip',
              mimeType: 'application/zip',
            );
          },
          saver: (
            bytes, {
            required filename,
            required mimeType,
            sharePositionOrigin,
          }) async {},
        ),
      ),
    );

    final csv = find.ancestor(
      of: find.text('CSV spreadsheets (zip)'),
      matching: find.byType(InkWell),
    );
    expect(csv, findsOneWidget);
    final focus = find.descendant(of: csv, matching: find.byType(Focus)).first;
    Focus.of(tester.element(
            find.descendant(of: focus, matching: find.byType(Padding)).first))
        .requestFocus();
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();

    await tester.tap(find.byKey(const ValueKey('export-download-button')));
    await tester.pump();
    await tester.pump();
    expect(requested, ExportFormat.csv);
  });

  testWidgets('MFA accounts: steps up, then retries the download once', (
    tester,
  ) async {
    var calls = 0;
    var steppedUp = 0;
    String? saved;
    await tester.pumpWidget(
      _host(
        DataExportSheet(
          downloader: (format) async {
            calls++;
            if (calls == 1) {
              throw _dioError(403, data: {'error': 'MfaRequired'});
            }
            return ExportFile(
              bytes: Uint8List(1),
              filename: 'after-mfa.json',
              mimeType: 'application/json',
            );
          },
          stepUp: (context) async {
            steppedUp++;
            return true;
          },
          saver: (
            bytes, {
            required filename,
            required mimeType,
            sharePositionOrigin,
          }) async {
            saved = filename;
          },
        ),
      ),
    );

    await tester.tap(find.byKey(const ValueKey('export-download-button')));
    await tester.pump();
    await tester.pump();
    await tester.pump();

    expect(steppedUp, 1);
    expect(calls, 2);
    expect(saved, 'after-mfa.json');
    await tester.pump(const Duration(seconds: 5));
  });

  testWidgets('MFA accounts: cancelling the step-up shows an error, no retry', (
    tester,
  ) async {
    var calls = 0;
    await tester.pumpWidget(
      _host(
        DataExportSheet(
          downloader: (format) async {
            calls++;
            throw _dioError(
              403,
              data: {'error': 'MfaRequired', 'message': 'Verify first'},
            );
          },
          stepUp: (context) async => false,
          saver: (
            bytes, {
            required filename,
            required mimeType,
            sharePositionOrigin,
          }) async {},
        ),
      ),
    );

    await tester.tap(find.byKey(const ValueKey('export-download-button')));
    await tester.pump();
    await tester.pump();

    expect(calls, 1);
    expect(find.byKey(const ValueKey('export-error')), findsOneWidget);
  });

  testWidgets('passes a share anchor rect from the Download button', (
    tester,
  ) async {
    Rect? origin;
    await tester.pumpWidget(
      _host(
        DataExportSheet(
          downloader: (format) async => ExportFile(
            bytes: Uint8List(1),
            filename: 'a.json',
            mimeType: 'application/json',
          ),
          saver: (
            bytes, {
            required filename,
            required mimeType,
            sharePositionOrigin,
          }) async {
            origin = sharePositionOrigin;
          },
        ),
      ),
    );

    await tester.tap(find.byKey(const ValueKey('export-download-button')));
    await tester.pump();
    await tester.pump();

    expect(origin, isNotNull);
    expect(origin!.isEmpty, isFalse);
    await tester.pump(const Duration(seconds: 5));
  });

  testWidgets('shows the server message from a failed export', (tester) async {
    await tester.pumpWidget(
      _host(
        DataExportSheet(
          downloader: (format) async =>
              throw _dioError(500, data: {'message': 'Export exploded'}),
          saver: (
            bytes, {
            required filename,
            required mimeType,
            sharePositionOrigin,
          }) async {},
        ),
      ),
    );

    await tester.tap(find.byKey(const ValueKey('export-download-button')));
    await tester.pump();
    await tester.pump();

    expect(find.text('Export exploded'), findsOneWidget);
  });

  test('filenameFromDisposition reads the header and falls back', () {
    expect(
      ExportService.filenameFromDisposition(
        'attachment; filename=buoy-export-2026-10-12.zip; filename*=UTF-8\'\'buoy-export-2026-10-12.zip',
        fallback: 'x',
      ),
      'buoy-export-2026-10-12.zip',
    );
    expect(
      ExportService.filenameFromDisposition(
        'attachment; filename="a/b/c.json"',
        fallback: 'x',
      ),
      'c.json',
    );
    expect(
      ExportService.filenameFromDisposition(null, fallback: 'fb.json'),
      'fb.json',
    );
  });
}
