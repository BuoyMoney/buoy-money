import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';

import '../config/portal_mode.dart';
import '../config/server_capabilities.dart';
import '../l10n/generated/app_localizations.dart';
import '../router/post_auth_redirect.dart';
import '../services/cloud_service.dart';
import '../utils/api_error.dart';
import '../utils/web_navigation.dart';
import '../widgets/cinematic/glass_settings.dart';
import '../widgets/step_up_mfa_dialog.dart';
import '../widgets/verify_email_dialog.dart';

/// `/cloud/link?state=..&name=..&callback=..` on the hosted app: a self-hosted server sent its administrator here
/// to approve it ("Link with BuoyAI Cloud"). Approving runs the second factor, makes a pairing code that confirms
/// the server on use, and sends the browser back to the server's callback with that code. The server's address is
/// shown large, because it is where the code goes: only approve a server you set up yourself.
///
/// Signed-in only: the router sends a signed-out visitor to sign-in or sign-up and back here with the same query.
class CloudLinkPage extends StatefulWidget {
  const CloudLinkPage({
    super.key,
    this.callback,
    this.state,
    this.name,
    this.service,
    this.stepUp,
    this.verifyEmail,
    this.navigate,
    this.animateBackground = true,
  });

  final String? callback;
  final String? state;
  final String? name;

  /// Injected in tests; the app uses the global service.
  final CloudService? service;

  /// Runs the step-up second factor. True when the user passed it. Injected in tests.
  final Future<bool> Function(BuildContext context)? stepUp;

  /// Confirms the account's email (the code dialog). True when confirmed. Injected in tests.
  final Future<bool> Function(BuildContext context)? verifyEmail;

  /// Leaves the app for [url] (same tab on web, the browser on a phone). Injected in tests.
  final Future<bool> Function(String url)? navigate;

  /// False in widget tests: the animated background never settles.
  final bool animateBackground;

  /// The path a server's link callback must have. Mirrors the hosted backend's check.
  static const callbackPath = '/api/CloudBridge/link/callback';

  /// [raw] when it is a server's link callback this page may send a code to: https (plain http only for a
  /// loopback host), the exact callback path, and no user info, query or fragment. Null otherwise.
  static Uri? parseCallback(String? raw) {
    if (raw == null || raw.isEmpty || raw.length > 2048) return null;
    if (raw.contains('?') ||
        raw.contains('#') ||
        raw.contains('@') ||
        raw.contains('%') ||
        raw.contains('..') ||
        raw.contains(String.fromCharCode(92)) ||
        raw.trim() != raw) {
      return null;
    }
    final uri = Uri.tryParse(raw);
    if (uri == null || uri.host.isEmpty || uri.userInfo.isNotEmpty) {
      return null;
    }
    final loopback =
        uri.host == 'localhost' ||
        uri.host == '127.0.0.1' ||
        uri.host == '[::1]' ||
        uri.host == '::1';
    if (uri.scheme != 'https' && !(uri.scheme == 'http' && loopback)) {
      return null;
    }
    if (uri.path != callbackPath) return null;
    return uri;
  }

  /// The address as the owner should read it: host, plus the port when the link names one.
  static String displayHost(Uri callback) =>
      callback.hasPort ? '${callback.host}:${callback.port}' : callback.host;

  @override
  State<CloudLinkPage> createState() => _CloudLinkPageState();
}

class _CloudLinkPageState extends State<CloudLinkPage> {
  bool _busy = false;
  bool _leaving = false;
  bool _noAccount = false;
  bool _needsEmail = false;
  String? _error;

  CloudService get _service => widget.service ?? cloudService;

  Future<bool> _verifyEmail(BuildContext context) async =>
      widget.verifyEmail != null
          ? await widget.verifyEmail!(context)
          : await showVerifyEmailDialog(context) == true;

  Future<bool> _navigate(String url) =>
      (widget.navigate ?? navigateToExternalUrl)(url);

  Future<bool> _stepUp(BuildContext context) async => widget.stepUp != null
      ? await widget.stepUp!(context)
      : await showStepUpMfaDialog(context, operation: 'ManageCloudServers') !=
            null;

  String? get _name {
    final n = widget.name?.trim();
    return (n == null || n.isEmpty) ? null : n;
  }

  /// Runs [action]; on a step-up request runs the second factor and tries once more. An account whose email is not
  /// confirmed confirms it first, then carries on. False when the person stopped (then [_needsEmail] may be set).
  Future<bool> _withStepUp(Future<void> Function() action) async {
    try {
      await action();
      return true;
    } catch (e) {
      if (CloudService.needsVerifiedEmail(e)) {
        if (!mounted || !await _verifyEmail(context)) {
          if (mounted) setState(() => _needsEmail = true);
          return false;
        }
        return _withStepUp(action);
      }
      if (CloudService.statusOf(e) == 403 &&
          CloudService.errorOf(e) == 'MfaRequired') {
        if (!mounted || !await _stepUp(context)) return false;
        await action();
        return true;
      }
      rethrow;
    }
  }

  Future<void> _approve(Uri callback, String state) async {
    if (_busy) return;
    final l10n = AppLocalizations.of(context);
    setState(() {
      _busy = true;
      _error = null;
      _needsEmail = false;
      _noAccount = false;
    });
    try {
      var redirect = '';
      final done = await _withStepUp(
        () async => redirect = await _service.approveCloudLink(
          callback: callback.toString(),
          state: state,
          name: _name,
        ),
      );
      if (!mounted || !done) return;
      if (redirect.isEmpty) throw StateError('no redirect');
      PostAuthRedirect.clear();
      setState(() => _leaving = true);
      final opened = await _navigate(redirect);
      if (!opened && mounted) {
        setState(() {
          _leaving = false;
          _error = l10n.cloudLinkOpenFailed;
        });
      }
    } catch (e) {
      if (!mounted) return;
      if (CloudService.statusOf(e) == 409 &&
          CloudService.codeOf(e) == 'no_cloud_account') {
        setState(() => _noAccount = true);
      } else {
        setState(
          () => _error = apiErrorMessage(e, fallback: l10n.cloudLinkFailed),
        );
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _cancel(Uri callback, String state) async {
    if (_busy) return;
    PostAuthRedirect.clear();
    final back = callback.replace(
      queryParameters: {'error': 'access_denied', 'state': state},
    );
    setState(() => _leaving = true);
    final opened = await _navigate(back.toString());
    if (!opened && mounted) {
      setState(() {
        _leaving = false;
        _error = AppLocalizations.of(context).cloudLinkOpenFailed;
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final callback = CloudLinkPage.parseCallback(widget.callback);
    final state = widget.state ?? '';
    final valid = callback != null && state.isNotEmpty && state.length <= 200;
    final selfHost = ServerEdition.current.value.isSelfHost;

    // A top-level page (outside the nav shell), so it paints the app's background itself.
    return GlassSubPage(
      title: l10n.cloudLinkTitle,
      backdrop: true,
      animateBackdrop: widget.animateBackground,
      showBack: false,
      maxWidth: 560,
      children: [
        if (selfHost)
          GlassNote(
            key: const ValueKey('cloud-link-hosted-only'),
            text: l10n.cloudLinkHostedOnly,
          )
        else if (!valid)
          GlassNote(
            key: const ValueKey('cloud-link-invalid'),
            text: l10n.cloudLinkInvalid,
            tone: GlassNoteTone.warning,
          )
        else
          _card(context, callback, state),
      ],
    );
  }

  Widget _card(BuildContext context, Uri callback, String state) {
    final l10n = AppLocalizations.of(context);
    final name = _name;

    return GlassSection(
      icon: Icons.dns_outlined,
      title: l10n.cloudLinkAddressLabel,
      highlight: true,
      children: [
        if (name != null) ...[
          Text(
            name,
            key: const ValueKey('cloud-link-name'),
            style: cineRowTitleStyle(),
          ),
          const SizedBox(height: 4),
        ],
        SelectableText(
          CloudLinkPage.displayHost(callback),
          key: const ValueKey('cloud-link-host'),
          style: GoogleFonts.firaCode(
            fontSize: 20,
            fontWeight: FontWeight.w600,
            color: cineText,
          ),
        ),
        const SizedBox(height: 16),
        Text(l10n.cloudLinkWhat, style: cineBodyStyle()),
        const SizedBox(height: 12),
        GlassNote(
          key: const ValueKey('cloud-link-warning'),
          text: l10n.cloudLinkWarning,
          tone: GlassNoteTone.warning,
        ),
        if (_needsEmail) ...[
          const SizedBox(height: 12),
          GlassNote(
            key: const ValueKey('cloud-link-needs-email'),
            text: l10n.cloudServersNeedsEmail,
            tone: GlassNoteTone.warning,
          ),
        ],
        if (_noAccount) ...[
          const SizedBox(height: 12),
          GlassNote(
            key: const ValueKey('cloud-link-no-account'),
            text: l10n.cloudLinkNoAccount,
          ),
          const SizedBox(height: 10),
          GlassSecondaryButton(
            key: const ValueKey('cloud-link-products'),
            // Pushed, so Back returns here with the same link to approve.
            onPressed: () => context.push(PortalMode.isActive ? '/' : '/settings/cloud'),
            label: l10n.cloudLinkSeeProducts,
          ),
        ],
        if (_error != null) ...[
          const SizedBox(height: 12),
          GlassNote(
            key: const ValueKey('cloud-link-error'),
            text: _error!,
            tone: GlassNoteTone.error,
          ),
        ],
        if (_leaving) ...[
          const SizedBox(height: 12),
          Text(
            l10n.cloudLinkLeaving,
            key: const ValueKey('cloud-link-leaving'),
            style: cineMutedStyle(size: 13),
          ),
        ],
        const SizedBox(height: 20),
        GlassPrimaryButton(
          key: const ValueKey('cloud-link-approve'),
          expand: true,
          onPressed: _busy || _leaving ? null : () => _approve(callback, state),
          label: _busy ? l10n.cloudLinkApproving : l10n.cloudLinkApprove,
        ),
        const SizedBox(height: 6),
        Center(
          child: GlassTextAction(
            key: const ValueKey('cloud-link-cancel'),
            color: cineMuted,
            onPressed: _busy || _leaving ? null : () => _cancel(callback, state),
            label: l10n.cloudCancel,
          ),
        ),
      ],
    );
  }
}
