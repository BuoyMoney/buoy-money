import 'package:dio/dio.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:buoy_ai/services/ai_service.dart';
import 'package:buoy_ai/services/api_client.dart';

/// Path-aware Dio adapter for the batch-categorize loop. Answers the review-stats
/// call with a fixed uncategorized count, and lets the test script each
/// /ai/categorize/batch round as either a success or a transport failure.
class _BatchAdapter implements HttpClientAdapter {
  _BatchAdapter({
    required this.uncategorized,
    required this.batchOutcomes,
    this.delay = Duration.zero,
    this.perRound = const [],
  });

  final int uncategorized;
  // One entry per batch call: true = succeed (categorize 5), false = throw.
  final List<bool> batchOutcomes;
  // How long each batch call takes, so the in-between progress ticker gets to run.
  final Duration delay;
  // How many each successful round categorizes (default 5).
  final List<int> perRound;
  int batchCalls = 0;
  int okRounds = 0;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    if (options.path.contains('review-stats')) {
      return ResponseBody.fromString(
        '{"totalNeedingReview":$uncategorized,"uncategorizedCount":$uncategorized,"aiEligibleCount":$uncategorized,'
        '"lowConfidenceCount":0,"thisMonthCount":0,"olderCount":$uncategorized}',
        200,
        headers: {
          Headers.contentTypeHeader: [Headers.jsonContentType],
        },
      );
    }
    if (options.path.contains('categorize/batch')) {
      final i = batchCalls++;
      if (delay > Duration.zero) await Future<void>.delayed(delay);
      final ok = i < batchOutcomes.length ? batchOutcomes[i] : false;
      if (!ok) {
        // Mirror the real failure: a dropped connection surfaces as a DioException.
        throw DioException.connectionError(
          requestOptions: options,
          reason: 'Software caused connection abort',
        );
      }
      final n = okRounds < perRound.length ? perRound[okRounds] : 5;
      okRounds++;
      return ResponseBody.fromString(
        '{"successfullyCategorized":$n,"highConfidence":$n,"needingReview":0,"failed":0}',
        200,
        headers: {
          Headers.contentTypeHeader: [Headers.jsonContentType],
        },
      );
    }
    return ResponseBody.fromString('{}', 200, headers: {
      Headers.contentTypeHeader: [Headers.jsonContentType],
    });
  }

  @override
  void close({bool force = false}) {}
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    // ApiClient reads the auth token from secure storage on every request.
    const channel =
        MethodChannel('plugins.it_nomads.com/flutter_secure_storage');
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(channel, (call) async => null);
  });

  test('a round that keeps failing stops gracefully with partial progress',
      () async {
    // 10 uncategorized, maxPerBatch 5 → 2 real rounds. Round 0 succeeds (5),
    // round 1 fails every retry → the run stops, keeps the 5, flags interrupted.
    apiClient.dio.httpClientAdapter = _BatchAdapter(
      uncategorized: 10,
      batchOutcomes: [true, false, false, false, false],
    );

    final result = await aiService.batchCategorize(maxPerBatch: 5);

    expect(result.successfullyCategorized, 5, reason: 'the good round is kept');
    expect(result.interrupted, isTrue,
        reason: 'the failed round flags interrupted');
  });

  test('a one-off blip is retried and the run completes', () async {
    // Round 1 fails once then succeeds on retry; both rounds ultimately land.
    apiClient.dio.httpClientAdapter = _BatchAdapter(
      uncategorized: 10,
      batchOutcomes: [true, false, true],
    );

    final result = await aiService.batchCategorize(maxPerBatch: 5);

    expect(result.successfullyCategorized, 10,
        reason: 'both rounds categorized');
    expect(result.interrupted, isFalse, reason: 'transient failure recovered');
  });

  test('progress never goes backwards and ends on the real total', () async {
    // Optimistic ticks between rounds must stay below what is actually done and the
    // last report must be the real count, so the line never jumps back.
    apiClient.dio.httpClientAdapter = _BatchAdapter(
      uncategorized: 15,
      batchOutcomes: [true, true, true],
    );
    final reports = <int>[];

    final result = await aiService.batchCategorize(
      maxPerBatch: 5,
      onProgress: (completed, total) => reports.add(completed),
    );

    expect(result.successfullyCategorized, 15);
    expect(reports, isNotEmpty);
    for (var i = 1; i < reports.length; i++) {
      expect(reports[i], greaterThanOrEqualTo(reports[i - 1]),
          reason: 'never backwards');
    }
    expect(reports.last, 15, reason: 'ends on the real total');
  });

  bool neverBackwards(List<int> r) =>
      [for (var i = 1; i < r.length; i++) r[i] >= r[i - 1]].every((ok) => ok);

  test('a slow round shows estimated progress before it lands', () async {
    // Each round takes 700 ms; the 150 ms ticker reports estimates in between.
    apiClient.dio.httpClientAdapter = _BatchAdapter(
      uncategorized: 15,
      batchOutcomes: [true, true, true],
      delay: const Duration(milliseconds: 700),
    );
    final reports = <int>[];

    await aiService.batchCategorize(
      maxPerBatch: 5,
      onProgress: (completed, total) => reports.add(completed),
    );

    expect(reports.where((r) => r % 5 != 0), isNotEmpty,
        reason: 'estimates between the real round totals');
    expect(reports.first, lessThan(5),
        reason: 'the first round shows progress before it finishes');
    expect(neverBackwards(reports), isTrue);
    expect(reports.last, 15);
  });

  test('a round that does fewer than estimated never moves the line back',
      () async {
    // The second round categorizes 3 of 5 after the ticker estimated up to 9.
    apiClient.dio.httpClientAdapter = _BatchAdapter(
      uncategorized: 15,
      batchOutcomes: [true, true, true, true],
      delay: const Duration(milliseconds: 900),
      perRound: [5, 3, 5, 2],
    );
    final reports = <int>[];

    final result = await aiService.batchCategorize(
      maxPerBatch: 5,
      onProgress: (completed, total) => reports.add(completed),
    );

    expect(result.successfullyCategorized, 15);
    expect(neverBackwards(reports), isTrue, reason: '$reports');
    expect(reports.last, 15);
  });
}
