import 'dart:convert';
import 'package:dio/dio.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:buoy_ai/services/api_client.dart';
import 'package:buoy_ai/services/categorization_rules_service.dart';

/// Captures the one request the rule service makes so the test can assert on
/// path and body instead of on a live backend.
class _CaptureAdapter implements HttpClientAdapter {
  RequestOptions? last;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    last = options;
    return ResponseBody.fromString(
      '{"id":12,"name":"Always: Dillons → Groceries"}',
      201,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
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
    const channel = MethodChannel('plugins.it_nomads.com/flutter_secure_storage');
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(channel, (call) async => null);
  });

  test('createRuleFromCategorization posts the from-categorization contract', () async {
    final adapter = _CaptureAdapter();
    apiClient.dio.httpClientAdapter = adapter;

    final rule = await CategorizationRulesService()
        .createRuleFromCategorization(transactionId: 41, categoryId: 10);

    expect(adapter.last!.path, contains('/categorizationrule/from-categorization'));
    expect(adapter.last!.method, 'POST');
    final body = adapter.last!.data is String
        ? jsonDecode(adapter.last!.data as String) as Map<String, dynamic>
        : adapter.last!.data as Map<String, dynamic>;
    expect(body['transactionId'], 41);
    expect(body['newCategoryId'], 10);
    expect(body['usePlaidEntityId'], isTrue);
    expect(body['shareWithFamily'], isTrue);
    expect(rule['id'], 12);
  });

  test('offers are OFF by default: nothing is asked until the user opts in', () async {
    final service = CategorizationRulesService();

    expect(await service.areOffersEnabled(), isFalse);
    expect(await service.shouldOfferRule('Dillons'), isFalse);
  });

  test('once enabled, an undeclined merchant is offered', () async {
    final service = CategorizationRulesService();
    await service.setOffersEnabled(true);

    expect(await service.areOffersEnabled(), isTrue);
    expect(await service.shouldOfferRule('Dillons'), isTrue);
  });

  test('turning offers back off stops asking even for undeclined merchants', () async {
    final service = CategorizationRulesService();
    await service.setOffersEnabled(true);
    await service.setOffersEnabled(false);

    expect(await service.shouldOfferRule('Dillons'), isFalse);
  });

  test('a declined merchant is not offered again, case-insensitively', () async {
    final service = CategorizationRulesService();
    await service.setOffersEnabled(true);

    await service.markOfferDeclined('Dillons');

    expect(await service.shouldOfferRule('DILLONS'), isFalse);
    expect(await service.shouldOfferRule('Amazon'), isTrue);
  });

  test('blank merchants are never offered', () async {
    final service = CategorizationRulesService();
    await service.setOffersEnabled(true);

    expect(await service.shouldOfferRule('  '), isFalse);
  });
}
