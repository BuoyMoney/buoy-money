import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:buoy_ai/services/api_client.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(
          const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
          (call) async => null,
        );
  });

  // FLUTTER-1B / #1057: sentry_dio's FailedRequestInterceptor ran before our
  // retry-aware filter and reported each retried 504 at error level.
  test(
    'sentry_dio failed-request capture is off (our filter reports instead)',
    () {
      final client = ApiClient();
      final sentryCapture = client.dio.interceptors.where(
        (i) => i.runtimeType.toString() == 'FailedRequestInterceptor',
      );
      expect(
        sentryCapture,
        isEmpty,
        reason:
            'addSentry() must pass captureFailedRequests: false, or every '
            'retried 504 is reported twice at error level',
      );
    },
  );
}
