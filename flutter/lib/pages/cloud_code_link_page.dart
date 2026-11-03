import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';

import '../config/portal_mode.dart';
import '../l10n/generated/app_localizations.dart';
import '../services/cloud_service.dart';
import '../utils/api_error.dart';
import '../widgets/cinematic/glass_settings.dart';
import '../widgets/step_up_mfa_dialog.dart';
import '../widgets/verify_email_dialog.dart';

/// `buoy-ai.com/cloud/link` (the Cloud portal; `cloud.buoymoney.com/link` redirects here): link a self-hosted server
/// by the code it shows, TV style. The owner types the code (or opens the QR link, which fills it in), sees which
/// server is asking (name, version, when), and approves it with their second factor, or denies it.
///
/// Signed-in only: the portal router sends a signed-out visitor to sign-in or sign-up and back here with the code.
class CloudCodeLinkPage extends StatefulWidget {
  const CloudCodeLinkPage({
    super.key,
    this.code,
    this.service,
    this.stepUp,
    this.verifyEmail,
    this.animateBackground = true,
  });

  /// The code from the address (`?code=XXXX-XXXX`), if any. Looked up straight away when it is well formed.
  final String? code;

  /// Injected in tests; the app uses the global service.
  final CloudService? service;

  /// Runs the step-up second factor. True when the user passed it. Injected in tests.
  final Future<bool> Function(BuildContext context)? stepUp;

  /// Confirms the account's email (the code dialog). True when confirmed. Injected in tests.
  final Future<bool> Function(BuildContext context)? verifyEmail;

  /// False in widget tests: the animated background never settles.
  final bool animateBackground;

  @override
  State<CloudCodeLinkPage> createState() => _CloudCodeLinkPageState();
}

/// An expected answer the page explains calmly (not a failure).
enum _Notice {
  none,
  badCode,
  alreadyDecided,
  tooManyTries,
  noAccount,
  needsEmail
}

class _CloudCodeLinkPageState extends State<CloudCodeLinkPage> {
  late final TextEditingController _codeField;
  CloudLinkSession? _session;
  bool _busy = false;
  _Notice _notice = _Notice.none;
  String? _error;

  CloudService get _service => widget.service ?? cloudService;

  Future<bool> _stepUp(BuildContext context) async => widget.stepUp != null
      ? await widget.stepUp!(context)
      : await showStepUpMfaDialog(context, operation: 'ManageCloudServers') !=
          null;

  Future<bool> _verifyEmail(BuildContext context) async =>
      widget.verifyEmail != null
          ? await widget.verifyEmail!(context)
          : await showVerifyEmailDialog(context) == true;

  @override
  void initState() {
    super.initState();
    final prefilled = CloudLinkSession.normalizeCode(widget.code);
    _codeField = TextEditingController(text: prefilled ?? widget.code ?? '');
    if (prefilled != null) {
      WidgetsBinding.instance.addPostFrameCallback((_) => _lookUp());
    }
  }

  @override
  void dispose() {
    _codeField.dispose();
    super.dispose();
  }

  void _reset({bool keepNotice = false}) {
    _error = null;
    if (!keepNotice) _notice = _Notice.none;
  }

  /// Turns a refusal into the calm note it deserves. Returns false for a real failure.
  bool _explain(Object e) {
    final status = CloudService.statusOf(e);
    final code = CloudService.codeOf(e) ?? CloudService.errorOf(e);
    final notice = switch ((status, code)) {
      (400, _) ||
      (404, 'invalid_code') ||
      (404, 'unknown_code') =>
        _Notice.badCode,
      (409, 'no_cloud_account') => _Notice.noAccount,
      (409, _) || (410, _) => _Notice.alreadyDecided,
      (429, _) => _Notice.tooManyTries,
      (403, 'email_verification_required') => _Notice.needsEmail,
      _ => _Notice.none,
    };
    if (notice == _Notice.none) return false;
    setState(() => _notice = notice);
    return true;
  }

  Future<void> _lookUp() async {
    if (_busy) return;
    final l10n = AppLocalizations.of(context);
    final code = CloudLinkSession.normalizeCode(_codeField.text);
    setState(() {
      _reset();
      _session = null;
    });
    if (code == null) {
      setState(() => _notice = _Notice.badCode);
      return;
    }
    setState(() => _busy = true);
    try {
      final session = await _service.linkSession(code);
      if (!mounted) return;
      setState(() {
        _codeField.text = code;
        _session = session;
      });
    } catch (e) {
      if (!mounted) return;
      if (!_explain(e)) {
        setState(
            () => _error = apiErrorMessage(e, fallback: l10n.cloudLinkFailed));
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  /// Runs [action]; on a step-up request runs the second factor and tries once more. Null when the person stopped.
  /// An account whose email is not confirmed yet confirms it here first (a new Cloud-only account has no other
  /// second factor), then the same code goes on to the step-up and is approved. Stopping that leaves the note.
  Future<CloudLinkSession?> _withStepUp(
      Future<CloudLinkSession> Function() action) async {
    try {
      return await action();
    } catch (e) {
      if (CloudService.needsVerifiedEmail(e)) {
        if (!mounted || !await _verifyEmail(context)) rethrow;
        return _withStepUp(action);
      }
      if (CloudService.statusOf(e) == 403 &&
          CloudService.errorOf(e) == 'MfaRequired') {
        if (!mounted || !await _stepUp(context)) return null;
        return await action();
      }
      rethrow;
    }
  }

  Future<void> _decide({required bool approve}) async {
    final session = _session;
    if (_busy || session == null) return;
    final l10n = AppLocalizations.of(context);
    final code = session.userCode.isNotEmpty
        ? session.userCode
        : CloudLinkSession.normalizeCode(_codeField.text)!;
    setState(() {
      _busy = true;
      _reset();
    });
    try {
      final updated = approve
          ? await _withStepUp(() => _service.approveLinkSession(code))
          : await _service.denyLinkSession(code);
      if (!mounted || updated == null) return;
      setState(() => _session = CloudLinkSession(
            userCode: code,
            name: updated.name.isNotEmpty ? updated.name : session.name,
            serverVersion: updated.serverVersion ?? session.serverVersion,
            createdAt: updated.createdAt ?? session.createdAt,
            expiresAt: updated.expiresAt ?? session.expiresAt,
            status: updated.status.isNotEmpty
                ? updated.status
                : (approve ? 'approved' : 'denied'),
          ));
    } catch (e) {
      if (!mounted) return;
      if (!_explain(e)) {
        setState(
            () => _error = apiErrorMessage(e, fallback: l10n.cloudLinkFailed));
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  void _startOver() {
    setState(() {
      _reset();
      _session = null;
      _codeField.clear();
    });
  }

  String _time(DateTime d) => MaterialLocalizations.of(context)
      .formatTimeOfDay(TimeOfDay.fromDateTime(d));

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final session = _session;

    return GlassSubPage(
      title: l10n.portalLinkTitle,
      intro: l10n.portalLinkIntro,
      backdrop: true,
      animateBackdrop: widget.animateBackground,
      fallbackPath: PortalMode.isActive ? '/' : '/settings/cloud',
      maxWidth: 560,
      children: [
        if (session == null) _entry(context) else _server(context, session),
        ..._notes(context),
      ],
    );
  }

  Widget _entry(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return GlassSection(
      key: const ValueKey('code-link-entry'),
      icon: Icons.pin_outlined,
      title: l10n.portalLinkCodeTitle,
      highlight: true,
      children: [
        Text(l10n.portalLinkCodeHelp, style: cineBodyStyle()),
        const SizedBox(height: 14),
        GlassTextField(
          fieldKey: const ValueKey('code-link-field'),
          controller: _codeField,
          label: l10n.portalLinkCodeLabel,
          hint: 'BCDF-GH23',
          autocorrect: false,
          textCapitalization: TextCapitalization.characters,
          onSubmitted: (_) => _lookUp(),
        ),
        const SizedBox(height: 16),
        GlassPrimaryButton(
          key: const ValueKey('code-link-continue'),
          expand: true,
          onPressed: _busy ? null : _lookUp,
          label: _busy ? l10n.portalLinkChecking : l10n.portalLinkContinue,
        ),
      ],
    );
  }

  Widget _server(BuildContext context, CloudLinkSession session) {
    final l10n = AppLocalizations.of(context);
    final approved = session.status == 'approved';
    final denied = session.status == 'denied';

    return GlassSection(
      key: const ValueKey('code-link-server'),
      icon: Icons.dns_outlined,
      title: l10n.portalLinkServerTitle,
      highlight: session.pending,
      children: [
        Text(
          session.name.isEmpty ? l10n.portalLinkUnnamed : session.name,
          key: const ValueKey('code-link-name'),
          style: GoogleFonts.sora(
              fontSize: 20, fontWeight: FontWeight.w600, color: cineText),
        ),
        const SizedBox(height: 8),
        GlassKeyValueRow(
            label: l10n.portalLinkCodeLabel,
            value: session.userCode,
            mono: true),
        if (session.serverVersion != null)
          GlassKeyValueRow(
              key: const ValueKey('code-link-version'),
              label: l10n.portalLinkVersion,
              value: session.serverVersion!),
        if (session.createdAt != null)
          GlassKeyValueRow(
              key: const ValueKey('code-link-asked'),
              label: l10n.portalLinkAsked,
              value: _time(session.createdAt!)),
        if (session.pending && session.expiresAt != null)
          GlassKeyValueRow(
              label: l10n.portalLinkExpires, value: _time(session.expiresAt!)),
        const SizedBox(height: 14),
        if (approved) ...[
          GlassNote(
            key: const ValueKey('code-link-approved'),
            text: l10n.portalLinkApproved,
            tone: GlassNoteTone.success,
          ),
          const SizedBox(height: 14),
          GlassPrimaryButton(
            key: const ValueKey('code-link-servers'),
            expand: true,
            onPressed: () =>
                context.go(PortalMode.isActive ? '/' : '/settings/cloud'),
            label: l10n.portalLinkSeeServers,
          ),
        ] else if (denied) ...[
          GlassNote(
            key: const ValueKey('code-link-denied'),
            text: l10n.portalLinkDenied,
          ),
          const SizedBox(height: 10),
          Center(
            child: GlassTextAction(
              key: const ValueKey('code-link-other'),
              onPressed: _startOver,
              label: l10n.portalLinkOtherCode,
            ),
          ),
        ] else ...[
          GlassNote(
            key: const ValueKey('code-link-warning'),
            text: l10n.portalLinkWarning,
            tone: GlassNoteTone.warning,
          ),
          const SizedBox(height: 18),
          GlassPrimaryButton(
            key: const ValueKey('code-link-approve'),
            expand: true,
            onPressed: _busy ? null : () => _decide(approve: true),
            label: _busy ? l10n.cloudLinkApproving : l10n.cloudLinkApprove,
          ),
          const SizedBox(height: 6),
          Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              GlassTextAction(
                key: const ValueKey('code-link-deny'),
                color: cineMuted,
                onPressed: _busy ? null : () => _decide(approve: false),
                label: l10n.portalLinkDeny,
              ),
              const SizedBox(width: 12),
              GlassTextAction(
                key: const ValueKey('code-link-other'),
                color: cineMuted,
                onPressed: _busy ? null : _startOver,
                label: l10n.portalLinkOtherCode,
              ),
            ],
          ),
        ],
      ],
    );
  }

  List<Widget> _notes(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final text = switch (_notice) {
      _Notice.badCode => l10n.portalLinkBadCode,
      _Notice.alreadyDecided => l10n.portalLinkAlreadyDecided,
      _Notice.tooManyTries => l10n.portalLinkTooManyTries,
      _Notice.noAccount => l10n.cloudLinkNoAccount,
      _Notice.needsEmail => l10n.cloudServersNeedsEmail,
      _Notice.none => null,
    };
    return [
      if (text != null) ...[
        const SizedBox(height: 12),
        GlassNote(
          key: ValueKey('code-link-note-${_notice.name}'),
          text: text,
          tone: _notice == _Notice.noAccount
              ? GlassNoteTone.info
              : GlassNoteTone.warning,
        ),
        if (_notice == _Notice.needsEmail && _session?.pending == true) ...[
          const SizedBox(height: 10),
          GlassSecondaryButton(
            key: const ValueKey('code-link-confirm-email'),
            onPressed: _busy ? null : () => _decide(approve: true),
            label: l10n.cloudConfirmEmailApprove,
          ),
        ],
        if (_notice == _Notice.noAccount) ...[
          const SizedBox(height: 10),
          GlassSecondaryButton(
            key: const ValueKey('code-link-products'),
            // Pushed, so Back returns here with the same code.
            onPressed: () =>
                context.push(PortalMode.isActive ? '/' : '/settings/cloud'),
            label: l10n.cloudLinkSeeProducts,
          ),
        ],
      ],
      if (_error != null) ...[
        const SizedBox(height: 12),
        GlassNote(
          key: const ValueKey('code-link-error'),
          text: _error!,
          tone: GlassNoteTone.error,
        ),
      ],
    ];
  }
}
