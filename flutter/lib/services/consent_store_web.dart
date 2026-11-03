import 'package:web/web.dart' as web;

/// Web implementation of the shared consent cookie. See `consent_store.dart` for the
/// contract; `marketing/public/consent.js` is the other half and must agree on the
/// cookie name and the exact `granted` / `denied` values.
const _cookieName = 'buoy_consent';

/// One year. Long enough that we are not re-asking returning visitors, short enough
/// that a stale choice eventually lapses rather than living forever.
const _maxAgeSeconds = 31536000;

String? readSharedConsent() {
  final raw = web.document.cookie;
  if (raw.isEmpty) return null;
  for (final part in raw.split(';')) {
    final entry = part.trim();
    if (entry.startsWith('$_cookieName=')) {
      return entry.substring(_cookieName.length + 1);
    }
  }
  return null;
}

void writeSharedConsent(bool granted) {
  final value = granted ? 'granted' : 'denied';
  // `Secure` is omitted on plain http so local dev (http://localhost) can still store
  // a choice — a Secure cookie is silently dropped over http, which would leave the
  // banner reappearing on every reload.
  final secure = web.window.location.protocol == 'https:' ? '; Secure' : '';
  web.document.cookie =
      '$_cookieName=$value; path=/; max-age=$_maxAgeSeconds; SameSite=Lax$secure';
}
