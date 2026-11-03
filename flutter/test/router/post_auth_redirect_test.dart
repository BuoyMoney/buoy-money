import 'package:buoy_ai/router/post_auth_redirect.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

void main() {
  setUp(() {
    SharedPreferences.setMockInitialValues({});
    PostAuthRedirect.now = DateTime.now;
    PostAuthRedirect.clear();
  });

  group('allowed targets', () {
    test('only the consent and link pages, as in-app paths', () {
      expect(PostAuthRedirect.isAllowed('/cloud/link?state=a&callback=b'),
          isTrue);
      expect(PostAuthRedirect.isAllowed('/connect?request=1'), isTrue);

      for (final bad in [
        null,
        '',
        '/dashboard',
        '/cloud/linked',
        '/cloud/link/extra',
        '//evil.test/cloud/link',
        'https://evil.test/cloud/link',
        'cloud/link',
        'javascript:alert(1)',
      ]) {
        expect(PostAuthRedirect.isAllowed(bad), isFalse, reason: '$bad');
      }
    });
  });

  group('remembering', () {
    test('a remembered page is where a new session lands', () {
      PostAuthRedirect.remember('/cloud/link?state=a');

      expect(PostAuthRedirect.destination(), '/cloud/link?state=a');
    });

    test('anything not allowed is ignored', () {
      PostAuthRedirect.remember('https://evil.test/');

      expect(PostAuthRedirect.destination(), '/dashboard');
    });

    test('clear forgets it', () {
      PostAuthRedirect.remember('/cloud/link?state=a');
      PostAuthRedirect.clear();

      expect(PostAuthRedirect.pending, isNull);
    });

    test('it goes stale after half an hour', () {
      var now = DateTime(2026, 10, 31, 12);
      PostAuthRedirect.now = () => now;
      PostAuthRedirect.remember('/cloud/link?state=a');

      now = now.add(const Duration(minutes: 31));

      expect(PostAuthRedirect.destination(), '/dashboard');
    });

    test('survives a reload through local storage', () async {
      PostAuthRedirect.remember('/cloud/link?state=a');
      await Future<void>.delayed(Duration.zero);
      final prefs = await SharedPreferences.getInstance();
      final saved = prefs.getString('post_auth_redirect');
      expect(saved, '/cloud/link?state=a');

      // A fresh start: memory empty, storage still holds it.
      PostAuthRedirect.forgetMemory();
      expect(PostAuthRedirect.pending, isNull);
      await PostAuthRedirect.restore();

      expect(PostAuthRedirect.pending, '/cloud/link?state=a');
    });
  });
}
