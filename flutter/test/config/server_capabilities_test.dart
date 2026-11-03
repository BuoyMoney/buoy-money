import 'package:buoy_ai/config/server_capabilities.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('a hosted server with no feature list keeps every screen', () {
    final c = ServerCapabilities.fromJson({'version': '1.5.29', 'edition': 'hosted'});

    expect(c.isSelfHost, isFalse);
    expect(c.hasBilling, isTrue);
    expect(c.hasBankConnection, isTrue);
    expect(c.googleSignIn, isTrue);
    expect(c.appleSignIn, isTrue);
    expect(c.anySocialSignIn, isTrue);
    expect(c.pushRelayUrl, isNull);
  });

  test('a server that sends nothing at all is treated as hosted', () {
    final c = ServerCapabilities.fromJson({});

    expect(c.isSelfHost, isFalse);
    expect(c.hasBilling, isTrue);
  });

  test('a self-hosted server hides billing, banks, Apple, and what it did not set up', () {
    final c = ServerCapabilities.fromJson({
      'edition': 'selfhost',
      'features': {'googleSignIn': false, 'passkeys': false, 'email': false},
      'pushRelayUrl': null,
    });

    expect(c.isSelfHost, isTrue);
    expect(c.hasBilling, isFalse);
    expect(c.hasBankConnection, isFalse);
    expect(c.appleSignIn, isFalse);
    expect(c.googleSignIn, isFalse);
    expect(c.anySocialSignIn, isFalse);
    expect(c.passkeys, isFalse);
    expect(c.email, isFalse);
  });

  test('a self-hosted server with Google set up still shows the Google button', () {
    final c = ServerCapabilities.fromJson({
      'edition': 'selfhost',
      'features': {'googleSignIn': true, 'passkeys': true, 'email': true},
      'pushRelayUrl': 'https://relay.example',
    });

    expect(c.anySocialSignIn, isTrue);
    expect(c.pushRelayUrl, 'https://relay.example');
  });

  test('a self-hosted server publishes its own Google client id', () {
    final c = ServerCapabilities.fromJson({'edition': 'selfhost', 'googleClientId': 'abc.apps.googleusercontent.com'});

    expect(c.googleClientId, 'abc.apps.googleusercontent.com');
    expect(ServerCapabilities.fromJson({}).googleClientId, isNull);
  });

  test('an empty relay address means no relay', () {
    expect(ServerCapabilities.fromJson({'pushRelayUrl': ''}).pushRelayUrl, isNull);
  });
}
