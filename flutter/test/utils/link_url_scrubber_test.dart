import 'package:flutter_test/flutter_test.dart';
import 'package:sentry_flutter/sentry_flutter.dart';
import 'package:buoy_ai/utils/link_url_scrubber.dart';

void main() {
  final hint = Hint();
  const direct = 'https://buoy-ai.com/app/cloud/link?state=SECRET&callback=http%3A%2F%2Fx';
  const detour =
      '/login?redirect=%2Fcloud%2Flink%3Fstate%3DSECRET%26callback%3Dhttp%253A%252F%252Fx';

  test('strips the query from the link page and from the sign-in detour', () {
    expect(LinkUrlScrubber.scrub(direct), 'https://buoy-ai.com/app/cloud/link');
    expect(LinkUrlScrubber.scrub(detour), '/login');
  });

  test('strips the pairing code from the portal sign-in detour', () {
    expect(
      LinkUrlScrubber.scrub(
        'https://buoy-ai.com/cloud/login?redirect=%2Flink%3Fcode%3DABCD-EFGH',
      ),
      'https://buoy-ai.com/cloud/login',
    );
    expect(
      LinkUrlScrubber.scrub('/cloud/register?redirect=%2Flink%3Fcode%3DABCD-EFGH'),
      '/cloud/register',
    );
    expect(LinkUrlScrubber.scrub('/link?code=ABCD-EFGH'), '/link');
    expect(LinkUrlScrubber.isSensitive('code=ABCD-EFGH'), isTrue);
    expect(LinkUrlScrubber.isSensitive('name=x&code=ABCD-EFGH'), isTrue);
  });

  test('events lose the pairing code in the portal detour', () {
    final ev = SentryEvent(
      request: SentryRequest(
        url: 'https://buoy-ai.com/cloud/login?redirect=%2Flink%3Fcode%3DABCD-EFGH',
        queryString: 'redirect=%2Flink%3Fcode%3DABCD-EFGH',
      ),
      breadcrumbs: [
        Breadcrumb(data: {'to': '/register?redirect=%2Flink%3Fcode%3DABCD-EFGH'}),
      ],
    );
    final out = LinkUrlScrubber.event(ev, hint);
    expect(out.request!.url, 'https://buoy-ai.com/cloud/login');
    expect(out.request!.queryString, isEmpty);
    expect(out.breadcrumbs!.first.data.toString(), isNot(contains('ABCD')));
  });

  test('does not scrub unrelated paths that merely contain "link" or "code"', () {
    for (final ok in [
      '/linked-accounts?x=1',
      '/settings/unlink?x=1',
      '/connect?zipcode=66601',
      '/promo-code',
      '/account?encode=1',
    ]) {
      expect(LinkUrlScrubber.scrub(ok), ok, reason: ok);
    }
  });

  test('leaves other URLs alone', () {
    expect(LinkUrlScrubber.scrub('/connect?request=abc'), '/connect?request=abc');
    expect(LinkUrlScrubber.scrub(null), isNull);
  });

  test('breadcrumbs lose the secret in message and data', () {
    final out = LinkUrlScrubber.breadcrumb(
      Breadcrumb(
        message: direct,
        data: {'to': detour, 'n': 1, 'nested': {'u': direct}},
      ),
      hint,
    )!;
    expect(out.message, isNot(contains('SECRET')));
    expect(out.data.toString(), isNot(contains('SECRET')));
    expect(out.data!['n'], 1);
  });

  test('events lose the secret in request, transaction and breadcrumbs', () {
    final ev = SentryEvent(
      request: SentryRequest(url: direct, queryString: 'state=SECRET'),
      transaction: detour,
      breadcrumbs: [Breadcrumb(message: direct)],
    );
    final out = LinkUrlScrubber.event(ev, hint);
    expect(out.request!.url, 'https://buoy-ai.com/app/cloud/link');
    expect(out.request!.queryString, isEmpty);
    expect(out.transaction, '/login');
    expect(out.breadcrumbs!.first.message, 'https://buoy-ai.com/app/cloud/link');
  });
  test('replaces a link code in an API path, keeping the rest', () {
    expect(
      LinkUrlScrubber.scrub(
          'https://buoy-ai.com/api/cloud-servers/link-sessions/BCDF-GH23/approve'),
      'https://buoy-ai.com/api/cloud-servers/link-sessions/[code]/approve',
    );
    expect(LinkUrlScrubber.scrub('/cloud-servers/link-sessions/BCDF-GH23'),
        '/cloud-servers/link-sessions/[code]');
    expect(LinkUrlScrubber.scrub('/cloud-servers/link-sessions/bcdf-gh23/deny'),
        '/cloud-servers/link-sessions/[code]/deny');
    expect(LinkUrlScrubber.isSensitive('/cloud-servers/link-sessions/BCDF-GH23'),
        isTrue);
    expect(LinkUrlScrubber.scrub('/v1/pairing/link-sessions/poll'),
        '/v1/pairing/link-sessions/poll');
    expect(LinkUrlScrubber.scrub('/cloud-servers/link-sessions/[code]'),
        '/cloud-servers/link-sessions/[code]');
  });

  test('Dio breadcrumbs lose the link code from the request URL', () {
    final out = LinkUrlScrubber.breadcrumb(
      Breadcrumb.http(
        url: Uri.parse(
            'https://buoy-ai.com/api/cloud-servers/link-sessions/BCDF-GH23/approve'),
        method: 'POST',
        statusCode: 500,
      ),
      hint,
    )!;
    expect(out.data.toString(), isNot(contains('BCDF-GH23')));
    expect(out.data.toString(), contains('link-sessions/[code]/approve'));
  });

  test('events lose a link code set in tags or the fingerprint', () {
    final ev = SentryEvent(
      tags: {'http.path': '/cloud-servers/link-sessions/BCDF-GH23'},
      fingerprint: ['dio', 'GET', '/cloud-servers/link-sessions/BCDF-GH23', '5xx'],
    );
    final out = LinkUrlScrubber.event(ev, hint);
    expect(out.tags!['http.path'], '/cloud-servers/link-sessions/[code]');
    expect(out.fingerprint, isNot(contains(contains('BCDF-GH23'))));
  });
}
