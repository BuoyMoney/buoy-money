// ignore_for_file: depend_on_referenced_packages
import 'package:flutter_test/flutter_test.dart';
import 'package:plugin_platform_interface/plugin_platform_interface.dart';
import 'package:google_sign_in_platform_interface/google_sign_in_platform_interface.dart';

import 'package:buoy_ai/config/server_capabilities.dart';
import 'package:buoy_ai/services/google_sign_in_service.dart';

class _FakePlatform extends Fake
    with MockPlatformInterfaceMixin
    implements GoogleSignInPlatform {
  final inits = <InitParameters>[];

  @override
  Future<void> init(InitParameters params) async => inits.add(params);

  @override
  Stream<AuthenticationEvent>? get authenticationEvents => null;
}

void main() {
  late _FakePlatform platform;

  String? idOf(InitParameters p) => p.clientId ?? p.serverClientId;

  setUp(() {
    platform = _FakePlatform();
    GoogleSignInPlatform.instance = platform;
    ServerEdition.current.value = ServerCapabilities.hosted;
    ServerEdition.resetHandshake();
  });

  tearDown(() {
    ServerEdition.current.value = ServerCapabilities.hosted;
  });

  test('waits for the handshake and uses the self-host client id', () async {
    final service = GoogleSignInService.forTesting(
        handshakeWait: const Duration(seconds: 5));
    addTearDown(service.dispose);
    ServerEdition.markHandshakeStarted();
    final pending = service.initialize();
    await Future<void>.delayed(const Duration(milliseconds: 10));
    expect(platform.inits, isEmpty);

    ServerEdition.current.value = const ServerCapabilities(
        edition: 'selfhost', googleClientId: 'selfhost-id.apps.googleusercontent.com');
    ServerEdition.markHandshakeDone();
    await pending;

    expect(platform.inits, hasLength(1));
    expect(idOf(platform.inits.single), 'selfhost-id.apps.googleusercontent.com');
  });

  test('re-initializes when the client id changes after init', () async {
    final service = GoogleSignInService.forTesting();
    addTearDown(service.dispose);
    ServerEdition.markHandshakeStarted(); // handshake never answers: bounded wait, hosted id
    await service.initialize();
    final hostedId = idOf(platform.inits.single);

    ServerEdition.current.value = const ServerCapabilities(
        edition: 'selfhost', googleClientId: 'selfhost-id.apps.googleusercontent.com');
    await service.initialize();

    expect(platform.inits, hasLength(2));
    expect(idOf(platform.inits.last), 'selfhost-id.apps.googleusercontent.com');
    expect(idOf(platform.inits.last), isNot(hostedId));
  });

  test('hosted keeps its client id and initializes only once', () async {
    final service = GoogleSignInService.forTesting();
    addTearDown(service.dispose);
    await service.initialize();
    await service.initialize();

    expect(platform.inits, hasLength(1));
    expect(idOf(platform.inits.single), isNotEmpty);
  });

  test('a server answer after the wait still switches to the self-host client id', () async {
    final service = GoogleSignInService.forTesting();
    addTearDown(service.dispose);
    ServerEdition.markHandshakeStarted();
    await service.initialize(); // the wait times out: hosted id for now
    expect(platform.inits, hasLength(1));

    // The version answer lands later; nothing calls initialize() again.
    ServerEdition.current.value = const ServerCapabilities(
        edition: 'selfhost', googleClientId: 'late-id.apps.googleusercontent.com');
    ServerEdition.markHandshakeDone();
    await Future<void>.delayed(const Duration(milliseconds: 100));

    expect(platform.inits, hasLength(2));
    expect(idOf(platform.inits.last), 'late-id.apps.googleusercontent.com');
  });
}
