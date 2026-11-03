import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:buoy_ai/config/app_config.dart';

/// A server picked at runtime on the sign-in screen (self-hosted), next to the build's default.
void main() {
  tearDown(() {
    AppConfig.customServerOriginForTest = null;
    AppConfig.sameOriginBuildForTest = null;
  });

  group('normalizeServerOrigin', () {
    test('adds https when there is no scheme', () {
      expect(AppConfig.normalizeServerOrigin('buoy.example.ts.net'), 'https://buoy.example.ts.net');
    });

    test('trims spaces, a trailing slash, and /api or /app', () {
      expect(AppConfig.normalizeServerOrigin('  https://buoy.example.com/  '), 'https://buoy.example.com');
      expect(AppConfig.normalizeServerOrigin('https://buoy.example.com/api'), 'https://buoy.example.com');
      expect(AppConfig.normalizeServerOrigin('https://buoy.example.com/api/'), 'https://buoy.example.com');
      expect(AppConfig.normalizeServerOrigin('https://buoy.example.com/app/'), 'https://buoy.example.com');
      expect(AppConfig.normalizeServerOrigin('https://buoy.example.com/app/api'), 'https://buoy.example.com');
    });

    test('keeps http, a port, and a sub-path', () {
      expect(AppConfig.normalizeServerOrigin('http://192.168.1.20:8080'), 'http://192.168.1.20:8080');
      expect(AppConfig.normalizeServerOrigin('192.168.1.20:8080/api'), 'https://192.168.1.20:8080');
      expect(AppConfig.normalizeServerOrigin('https://example.com/buoy/api'), 'https://example.com/buoy');
    });

    test('lowercases the scheme and host and drops a query', () {
      expect(AppConfig.normalizeServerOrigin('HTTPS://Buoy.Example.COM/?x=1'), 'https://buoy.example.com');
    });

    test('rejects what cannot be a server address', () {
      expect(AppConfig.normalizeServerOrigin(''), isNull);
      expect(AppConfig.normalizeServerOrigin('   '), isNull);
      expect(AppConfig.normalizeServerOrigin('not a url'), isNull);
      expect(AppConfig.normalizeServerOrigin('ftp://example.com'), isNull);
      expect(AppConfig.normalizeServerOrigin('https://'), isNull);
      expect(AppConfig.normalizeServerOrigin('https://user:pw@example.com'), isNull);
    });

    test('flags http as insecure', () {
      expect(AppConfig.isInsecureOrigin('http://192.168.1.20'), isTrue);
      expect(AppConfig.isInsecureOrigin('https://buoy.example.com'), isFalse);
    });
  });

  group('baseUrl', () {
    test('hosted default: no custom server, baseUrl is the build default', () {
      expect(AppConfig.isCustomServer, isFalse);
      expect(AppConfig.isNonHostedServer, isFalse);
      expect(AppConfig.canChooseServer, isTrue);
      expect(AppConfig.baseUrl, AppConfig.defaultBaseUrl);
      // The test VM is a development, non-web build: the emulator address.
      expect(AppConfig.defaultBaseUrl, 'http://10.0.2.2:5000/api');
    });

    test('a custom server becomes <origin>/api', () {
      AppConfig.customServerOriginForTest = 'https://buoy.example.ts.net';
      expect(AppConfig.isCustomServer, isTrue);
      expect(AppConfig.isNonHostedServer, isTrue);
      expect(AppConfig.baseUrl, 'https://buoy.example.ts.net/api');
    });

    test('the self-host web build ignores a saved server and offers no picker', () {
      AppConfig.sameOriginBuildForTest = true;
      AppConfig.customServerOriginForTest = 'https://buoy.example.ts.net';
      expect(AppConfig.customServerOrigin, isNull);
      expect(AppConfig.isCustomServer, isFalse);
      expect(AppConfig.isNonHostedServer, isTrue);
      expect(AppConfig.canChooseServer, isFalse);
    });
  });

  group('persistence', () {
    test('save and load round trip, and clearing goes back to the default', () async {
      SharedPreferences.setMockInitialValues({});
      await AppConfig.saveCustomServer('buoy.example.ts.net/api/');
      AppConfig.customServerOriginForTest = null;

      await AppConfig.loadCustomServer();
      expect(AppConfig.customServerOrigin, 'https://buoy.example.ts.net');
      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getString(AppConfig.customServerPrefKey), 'https://buoy.example.ts.net');

      await AppConfig.saveCustomServer(null);
      await AppConfig.loadCustomServer();
      expect(AppConfig.customServerOrigin, isNull);
      expect(prefs.getString(AppConfig.customServerPrefKey), isNull);
    });

    test('nothing saved: load leaves the default in place', () async {
      SharedPreferences.setMockInitialValues({});
      await AppConfig.loadCustomServer();
      expect(AppConfig.isCustomServer, isFalse);
      expect(AppConfig.baseUrl, AppConfig.defaultBaseUrl);
    });
  });
}
