import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

// Guards the Android http scope: a blanket usesCleartextTraffic would let a typed public
// http:// address send passwords in the clear (iOS only opens local networking).
void main() {
  test('manifest uses a network security config, not blanket cleartext', () {
    final manifest = File('android/app/src/main/AndroidManifest.xml').readAsStringSync();
    expect(manifest, isNot(contains('usesCleartextTraffic="true"')));
    expect(manifest, contains('android:networkSecurityConfig="@xml/network_security_config"'));
  });

  test('network security config denies cleartext by default', () {
    final xml = File('android/app/src/main/res/xml/network_security_config.xml').readAsStringSync();
    expect(xml, contains('<base-config cleartextTrafficPermitted="false"'));
    final domainConfig = RegExp(r'<domain-config[^>]*>([\s\S]*?)</domain-config>').firstMatch(xml)!;
    final domains = RegExp(r'<domain[^>]*>([^<]+)</domain>')
        .allMatches(domainConfig.group(1)!)
        .map((m) => m.group(1)!)
        .toList();
    expect(domains, containsAll(['localhost', '10.0.2.2']));
    for (final d in domains) {
      expect(d.contains('.') && !d.endsWith('.arpa') && RegExp(r'[a-z]{2,}\.(com|net|org|io)$').hasMatch(d), isFalse,
          reason: 'public domain $d must not be cleartext-allowed');
    }
  });
}
