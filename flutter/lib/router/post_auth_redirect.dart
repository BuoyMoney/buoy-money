import 'package:flutter/foundation.dart' show visibleForTesting;
import 'package:shared_preferences/shared_preferences.dart';

import '../config/portal_mode.dart';

/// Where to go after signing in, for the few pages a signed-out visitor may be
/// sent to from outside the app: the OAuth consent page (`/connect`) and the
/// "Link your server" page (`/cloud/link`). The router remembers the page when
/// it bounces the visitor to sign-in or sign-up, and every place that lands a
/// fresh session (password, Google, Apple, a new account, the name page) asks
/// [destination] instead of going straight to the dashboard.
///
/// In the Cloud portal (buoy-ai.com/cloud/, [PortalMode]) the only such page is
/// its own link page, `/link`; the two sets never mix, so a target saved in one
/// is ignored by the other (they share the browser's storage).
///
/// Only those paths are ever remembered, and only as an in-app path, so a
/// crafted `?redirect=` can never send someone off the app. The target is also
/// kept in local storage for [ttl], so it survives a page reload or an OAuth
/// round trip during sign-up. It is cleared once the page is reached.
class PostAuthRedirect {
  PostAuthRedirect._();

  static const _appPaths = {'/connect', '/cloud/link'};
  static const _portalPaths = {'/link'};

  /// The pages that may be remembered here: the app's two, or the portal's link page.
  static Set<String> get allowedPaths =>
      PortalMode.isActive ? _portalPaths : _appPaths;
  static const ttl = Duration(minutes: 30);
  static const _key = 'post_auth_redirect';
  static const _atKey = 'post_auth_redirect_at';

  static String? _target;
  static DateTime? _savedAt;

  /// Overridable clock for tests.
  static DateTime Function() now = DateTime.now;

  /// True for an in-app path (no scheme, no host) whose path is one of [allowedPaths].
  static bool isAllowed(String? target) {
    if (target == null || target.isEmpty) return false;
    if (!target.startsWith('/') || target.startsWith('//')) return false;
    if (target.contains(String.fromCharCode(92))) return false;
    final uri = Uri.tryParse(target);
    if (uri == null || uri.hasScheme || uri.hasAuthority) return false;
    return allowedPaths.contains(uri.path);
  }

  /// Remembers [target] when it is allowed. Ignores anything else.
  static void remember(String? target) {
    if (!isAllowed(target)) return;
    _target = target;
    _savedAt = now();
    _persist();
  }

  /// The remembered page, or null when there is none or it is older than [ttl].
  static String? get pending {
    final target = _target;
    final at = _savedAt;
    if (target == null || at == null) return null;
    if (now().difference(at) > ttl) {
      clear();
      return null;
    }
    return target;
  }

  /// Where a freshly signed-in person should land: the remembered page, else
  /// [fallback], else the dashboard (the portal home in the Cloud portal).
  static String destination([String? fallback]) =>
      pending ?? fallback ?? PortalMode.home;

  static void clear() {
    if (_target == null && _savedAt == null) return;
    _target = null;
    _savedAt = null;
    _persist();
  }

  /// Forgets the in-memory copy only, as a fresh start would. Tests.
  @visibleForTesting
  static void forgetMemory() {
    _target = null;
    _savedAt = null;
  }

  /// Loads a target saved before a reload. Call once at startup.
  static Future<void> restore() async {
    try {
      final prefs = await SharedPreferences.getInstance();
      final target = prefs.getString(_key);
      final at = DateTime.tryParse(prefs.getString(_atKey) ?? '');
      if (_target != null) return; // something newer was remembered meanwhile
      if (target == null || at == null || !isAllowed(target)) return;
      if (now().difference(at) > ttl) {
        await prefs.remove(_key);
        await prefs.remove(_atKey);
        return;
      }
      _target = target;
      _savedAt = at;
    } catch (_) {
      // No storage: the target lives for this session only.
    }
  }

  static Future<void> _persist() async {
    final target = _target;
    final at = _savedAt;
    try {
      final prefs = await SharedPreferences.getInstance();
      if (target == null || at == null) {
        await prefs.remove(_key);
        await prefs.remove(_atKey);
      } else {
        await prefs.setString(_key, target);
        await prefs.setString(_atKey, at.toIso8601String());
      }
    } catch (_) {
      // Storage is a convenience here; memory still holds the target.
    }
  }
}
