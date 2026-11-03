import 'package:buoy_ai/services/ai_status_service.dart';
import 'package:flutter_test/flutter_test.dart';

class _FakeService extends AiStatusService {
  _FakeService({this.status, this.fail = false});

  final AiStatus? status;
  final bool fail;
  int fetches = 0;

  @override
  Future<AiStatus> fetch() async {
    fetches++;
    if (fail) throw Exception('404');
    return status!;
  }
}

AiStatus _status({
  bool enabled = true,
  String provider = 'ollama',
  String edition = 'selfhost',
  List<AiFeatureStatus> features = const [],
}) =>
    AiStatus(enabled: enabled, provider: provider, edition: edition, features: features);

const _chatOff = AiFeatureStatus(id: 'chat', name: 'Chat', status: 'off', note: '');
const _catOn = AiFeatureStatus(id: 'categorization', name: 'Categorization', status: 'works', note: '');
const _insightsLimited = AiFeatureStatus(id: 'insights', name: 'Insights', status: 'limited', note: '');

void main() {
  group('AiStatus.fromJson', () {
    test('reads the server answer, including admin details', () {
      final status = AiStatus.fromJson({
        'enabled': true,
        'provider': 'ollama',
        'edition': 'selfhost',
        'features': [
          {'id': 'categorization', 'name': 'Transaction categorization', 'status': 'works', 'note': 'Fine on 7B.'},
          {'id': 'chat', 'name': 'Chat', 'status': 'off', 'note': 'Gemini only.'},
        ],
        'details': {
          'baseUrl': 'http://localhost:11434/v1',
          'model': 'qwen2.5:7b',
          'embeddingModel': '',
          'apiKeySet': false,
          'setupProblem': null,
        },
      });

      expect(status.enabled, isTrue);
      expect(status.provider, 'ollama');
      expect(status.features.map((f) => f.id), ['categorization', 'chat']);
      expect(status.features[1].isOff, isTrue);
      expect(status.details!.model, 'qwen2.5:7b');
      expect(status.details!.apiKeySet, isFalse);
    });

    test('a non-admin answer has no details', () {
      final status = AiStatus.fromJson({'enabled': false, 'provider': 'none', 'edition': 'selfhost', 'features': []});
      expect(status.details, isNull);
      expect(status.enabled, isFalse);
    });

    test('ignores junk in the feature list instead of crashing', () {
      final status = AiStatus.fromJson({
        'enabled': true,
        'provider': 'gemini',
        'edition': 'hosted',
        'features': [
          'nope',
          {'id': 'chat', 'name': 'Chat', 'status': 'works', 'note': ''},
        ],
      });
      expect(status.features, hasLength(1));
    });
  });

  group('AiAvailability', () {
    test('before the server has answered, everything shows (hosted must never lose a feature)', () {
      final availability = AiAvailability(service: _FakeService(fail: true));
      expect(availability.loaded, isFalse);
      expect(availability.aiOff, isFalse);
      expect(availability.featureOn('chat'), isTrue);
      expect(availability.featureOn('categorization'), isTrue);
    });

    test('a failed lookup (older server, offline) keeps everything visible and is not retried by itself', () async {
      final service = _FakeService(fail: true);
      final availability = AiAvailability(service: service);

      await availability.ensureLoaded();
      await availability.ensureLoaded();

      expect(service.fetches, 1);
      expect(availability.featureOn('chat'), isTrue);
      expect(availability.aiOff, isFalse);
    });

    test('AI off hides every feature', () async {
      final availability = AiAvailability(service: _FakeService(status: _status(enabled: false, provider: 'none')));
      await availability.ensureLoaded();

      expect(availability.aiOff, isTrue);
      expect(availability.featureOn('categorization'), isFalse);
      expect(availability.featureOn('chat'), isFalse);
      expect(availability.featureOn('insights'), isFalse);
    });

    test('hides only the features the server marks off; limited still shows', () async {
      final availability = AiAvailability(
        service: _FakeService(status: _status(features: const [_catOn, _chatOff, _insightsLimited])),
      );
      await availability.ensureLoaded();

      expect(availability.featureOn('categorization'), isTrue);
      expect(availability.featureOn('insights'), isTrue);
      expect(availability.featureOn('chat'), isFalse);
      // A feature the server did not list (a newer app than server) stays on.
      expect(availability.featureOn('something-new'), isTrue);
    });

    test('loads once and shares the answer; refresh asks again', () async {
      final service = _FakeService(status: _status(features: const [_catOn]));
      final availability = AiAvailability(service: service);

      await Future.wait([availability.ensureLoaded(), availability.ensureLoaded()]);
      expect(service.fetches, 1);

      await availability.refresh();
      expect(service.fetches, 2);
    });

    test('notifies listeners when the answer arrives and when it is reset on sign-out', () async {
      final availability = AiAvailability(service: _FakeService(status: _status(enabled: false)));
      var notified = 0;
      availability.addListener(() => notified++);

      await availability.ensureLoaded();
      expect(notified, 1);
      expect(availability.aiOff, isTrue);

      availability.reset();
      expect(notified, 2);
      expect(availability.loaded, isFalse);
      expect(availability.aiOff, isFalse);
    });
  });
}
