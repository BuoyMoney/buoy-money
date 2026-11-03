import 'dart:async';

import 'package:flutter/foundation.dart'
    show TargetPlatform, defaultTargetPlatform, kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter/services.dart' show Clipboard, ClipboardData;
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:qr_flutter/qr_flutter.dart';
import 'package:provider/provider.dart';
import 'package:url_launcher/url_launcher.dart';

import '../config/server_capabilities.dart';
import '../l10n/generated/app_localizations.dart';
import '../providers/auth_provider.dart';
import '../services/ai_status_service.dart';
import '../services/ai_usage_service.dart';
import '../services/cloud_service.dart';
import '../services/simplefin_service.dart';
import '../utils/api_error.dart';
import '../utils/formatters.dart';
import '../widgets/app_toast.dart';
import '../widgets/cinematic/glass_settings.dart';
import '../widgets/step_up_mfa_dialog.dart';

/// Settings > BuoyAI Cloud. The optional paid add-ons in one place: link this server to the Cloud, connect a
/// bank through it, see the AI allowance, open billing, and (on the hosted service) pick add-ons. Each
/// section loads on its own and hides when it does not apply to this server.
///
/// On a self-hosted server that is not linked yet, the page leads with one action, "Link with BuoyAI Cloud": the
/// server shows a short code and a QR code, the owner approves it on the Cloud page from any device, and the card
/// notices by itself. Nothing on it offers a Cloud bank connection until the server is linked.
///
/// On the hosted service, "Your servers" shows only to someone who bought a Cloud add-on or already has a server
/// (`GET /api/cloud-servers/visibility`). With [portal] (the Cloud portal home at buoy-ai.com/cloud/) the page is
/// just the Cloud: link a server by code, add-ons, billing and your servers (always shown), plus sign out.
class CloudPage extends StatefulWidget {
  const CloudPage(
      {super.key,
      this.service,
      this.aiStatusLoader,
      this.aiUsageLoader,
      this.open,
      this.stepUp,
      this.linked = false,
      this.linkError,
      this.portal = false,
      this.animateBackground = true});

  /// Injected in tests; the app uses the global service.
  final CloudService? service;
  final Future<AiStatus> Function()? aiStatusLoader;
  final Future<AiUsageSummary> Function()? aiUsageLoader;

  /// Opens a web address in the browser (bank sign-in, checkout, the Cloud approve page). Injected in tests.
  final Future<void> Function(String url)? open;

  /// Runs the step-up second factor before a pairing code or a confirm. Injected in tests.
  final Future<bool> Function(BuildContext context)? stepUp;

  /// Opened by the server's older link callback: `?linked=1` after a link, `?link_error=` with a short reason after a
  /// failed one.
  final bool linked;
  final String? linkError;

  /// The Cloud portal home (buoy-ai.com/cloud/): its own top-level page, outside the app's nav shell.
  final bool portal;

  /// False in widget tests: the portal's animated background never settles.
  final bool animateBackground;

  @override
  State<CloudPage> createState() => _CloudPageState();
}

class _CloudPageState extends State<CloudPage> {
  /// What the link card last read. Null until it loads, and for anyone but the server administrator.
  CloudPairingStatus? _pairing;

  /// The link card has answered (or failed): until then `_pairing == null` means "still loading", not "not linked".
  bool _pairingResolved = false;

  /// The Cloud bank feed is on here but this server is not linked: the link card says so.
  bool _bankNeedsLink = false;

  /// The server said this person is not its administrator (a household member): linking and billing are the
  /// owner's job, and the cards say so.
  bool _member = false;

  void _onMember(bool member) {
    if (!mounted || member == _member) return;
    setState(() => _member = member);
  }

  void _onPairing(CloudPairingStatus? status) {
    if (!mounted) return;
    setState(() {
      _pairing = status;
      _pairingResolved = true;
    });
  }

  /// Bumped to make the link card read the pairing again (the link went away somewhere else on the page).
  int _pairingEpoch = 0;

  void _reloadPairing() {
    if (!mounted) return;
    setState(() => _pairingEpoch++);
  }

  void _onBankNeedsLink(bool needsLink) {
    if (!mounted || needsLink == _bankNeedsLink) return;
    setState(() => _bankNeedsLink = needsLink);
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final svc = widget.service ?? cloudService;
    final opener = widget.open ?? _openExternal;
    final pairing = _pairing;
    final canLink =
        pairing != null && pairing.configured && !pairing.paired;
    final stepUp = widget.stepUp ?? _stepUpManageServers;

    if (widget.portal) {
      return GlassSubPage(
        title: l10n.portalHomeTitle,
        intro: l10n.portalHomeIntro,
        backdrop: true,
        animateBackdrop: widget.animateBackground,
        showBack: false,
        children: [
          const _PortalActions(),
          _ProductsSection(service: svc, open: opener, portal: true),
          const _BillingCard(portal: true),
          _ServersSection(service: svc, stepUp: stepUp),
        ],
      );
    }

    return ValueListenableBuilder<ServerCapabilities>(
      valueListenable: ServerEdition.current,
      builder: (context, caps, _) => GlassSubPage(
        title: l10n.cloudTitle,
        intro: l10n.cloudIntro,
        children: [
          if (caps.isSelfHost)
            _PairingSection(
                key: ValueKey('cloud-pairing-$_pairingEpoch'),
                service: svc,
                open: opener,
                linked: widget.linked,
                linkError: widget.linkError,
                bankNeedsLink: _bankNeedsLink,
                onStatus: _onPairing,
                onMember: _onMember),
          if (caps.isSelfHost)
            _BankSection(
                service: svc,
                open: opener,
                paired: pairing?.paired,
                linkCardShown: canLink,
                member: _member,
                onNeedsLink: _onBankNeedsLink,
                onLinkLost: _reloadPairing),
          _AiSection(
              statusLoader: widget.aiStatusLoader,
              usageLoader: widget.aiUsageLoader),
          _BillingCard(
              onReturn: _reloadPairing,
              loading: caps.isSelfHost && !_pairingResolved,
              notLinked: caps.isSelfHost && canLink,
              ownerManages: caps.isSelfHost && _member),
          if (!caps.isSelfHost) _ProductsSection(service: svc, open: opener),
          if (!caps.isSelfHost) _ServersGate(service: svc, stepUp: stepUp),
        ],
      ),
    );
  }
}

Future<bool> _stepUpManageServers(BuildContext context) async =>
    await showStepUpMfaDialog(context, operation: 'ManageCloudServers') != null;

Future<void> _openExternal(String url) async {
  final uri = Uri.tryParse(url);
  if (uri != null && uri.scheme == 'https') {
    await launchUrl(uri, mode: LaunchMode.externalApplication);
  }
}

/// The portal's own actions: link a server by the code it shows, and sign out.
class _PortalActions extends StatelessWidget {
  const _PortalActions();

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return _SectionCard(
      key: const ValueKey('portal-actions'),
      icon: Icons.link_rounded,
      title: l10n.portalLinkTitle,
      highlight: true,
      children: [
        Text(l10n.portalHomeLinkBody, style: cineBodyStyle()),
        const SizedBox(height: 14),
        GlassPrimaryButton(
          key: const ValueKey('portal-link-server'),
          expand: true,
          icon: Icons.pin_outlined,
          onPressed: () => context.push('/link'),
          label: l10n.portalHomeLinkButton,
        ),
        const SizedBox(height: 6),
        Center(
          child: GlassTextAction(
            key: const ValueKey('portal-sign-out'),
            color: cineMuted,
            onPressed: () => context.read<AuthProvider>().logout(),
            label: l10n.portalSignOut,
          ),
        ),
      ],
    );
  }
}

/// "Your servers" on the hosted app's Settings, only for someone the server says should see it: a Cloud customer, or
/// someone who already has a server. Everyone else never sees server linking. Hidden while it asks and if it cannot.
class _ServersGate extends StatefulWidget {
  const _ServersGate({required this.service, required this.stepUp});

  final CloudService service;
  final Future<bool> Function(BuildContext context) stepUp;

  @override
  State<_ServersGate> createState() => _ServersGateState();
}

class _ServersGateState extends State<_ServersGate> {
  bool _show = false;

  @override
  void initState() {
    super.initState();
    _check();
  }

  Future<void> _check() async {
    var show = false;
    try {
      show = await widget.service.serversVisible();
    } catch (_) {
      // Cannot tell: keep it hidden.
    }
    if (mounted && show) setState(() => _show = true);
  }

  @override
  Widget build(BuildContext context) => _show
      ? _ServersSection(service: widget.service, stepUp: widget.stepUp)
      : const SizedBox.shrink(key: ValueKey('cloud-servers-hidden'));
}

/// One card on the page, in the app's glass style.
class _SectionCard extends StatelessWidget {
  const _SectionCard(
      {required this.title,
      required this.children,
      this.icon = Icons.cloud_outlined,
      this.highlight = false,
      super.key});

  final String title;
  final IconData icon;
  final bool highlight;
  final List<Widget> children;

  @override
  Widget build(BuildContext context) => GlassSection(
      icon: icon, title: title, highlight: highlight, children: children);
}

// ---------------------------------------------------------------- link this server

class _PairingSection extends StatefulWidget {
  const _PairingSection(
      {super.key,
      required this.service,
      required this.open,
      required this.onStatus,
      this.onMember,
      this.linked = false,
      this.linkError,
      this.bankNeedsLink = false});

  final CloudService service;

  /// Opens the Cloud approve page in the browser (a new tab on the web, so this screen keeps waiting).
  final Future<void> Function(String url) open;

  /// Tells the page what the pairing is, so the other cards can follow it.
  final ValueChanged<CloudPairingStatus?> onStatus;

  /// Tells the page whether the server answered "not the administrator" (true) or showed the pairing (false).
  final ValueChanged<bool>? onMember;

  /// The page was opened by the older link callback after a successful link (`?linked=1`).
  final bool linked;

  /// The page was opened by the older link callback after a failed link (`?link_error=`): a short reason.
  final String? linkError;

  /// The Cloud bank feed is on but waits for this link: say so on the card.
  final bool bankNeedsLink;

  @override
  State<_PairingSection> createState() => _PairingSectionState();
}

class _PairingSectionState extends State<_PairingSection>
    with WidgetsBindingObserver {
  /// How often the card asks this server whether the owner approved the code.
  static const _pollEvery = Duration(seconds: 3);

  /// After this device's own countdown reaches zero, how long to keep asking the server before giving up on a
  /// reply. The server decides when a code is over; the countdown only runs on this device's clock.
  static const _expiryGrace = Duration(seconds: 30);

  final _code = TextEditingController();
  final _name = TextEditingController();
  CloudPairingStatus? _status;
  bool _loading = true;
  bool _failed = false;
  bool _busy = false;
  bool _showCode = false;
  late bool _justLinked = widget.linked;
  late String? _linkError = widget.linkError;
  String? _error;

  /// The code on screen while the server waits for the owner's approval.
  CloudDeviceLink? _device;

  /// How the last code link ended when it did not link (expired, turned down, failed).
  CloudDeviceLinkState? _deviceEnd;
  String? _deviceMessage;
  Timer? _poll;
  bool _polling = false;

  /// Runs from the moment this device's countdown reaches zero until the server says what became of the code.
  Timer? _graceTimer;

  /// Cancel was pressed and the server has not confirmed it yet. The code stays on screen (and polling goes on)
  /// until it does: the server could still pair if the owner approves meanwhile.
  bool _cancelling = false;

  /// The server could not be told to stop waiting. Shown on the code card, with Cancel to try again.
  String? _cancelError;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _load();
  }

  @override
  void dispose() {
    _poll?.cancel();
    _graceTimer?.cancel();
    WidgetsBinding.instance.removeObserver(this);
    _code.dispose();
    _name.dispose();
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (_device == null) return;
    switch (state) {
      case AppLifecycleState.resumed:
        // Back from approving in the browser: ask at once, then keep asking.
        _startPolling();
        _pollNow();
      case AppLifecycleState.paused:
      case AppLifecycleState.hidden:
      case AppLifecycleState.detached:
        _poll?.cancel();
      case AppLifecycleState.inactive:
        break;
    }
  }

  void _setStatus(CloudPairingStatus? status) {
    _status = status;
    widget.onStatus(status);
  }

  Future<void> _load({bool resume = true}) async {
    try {
      final status = await widget.service.pairingStatus();
      if (!mounted) return;
      setState(() {
        _setStatus(status);
        _loading = false;
        _failed = false;
      });
      if (resume && status != null && status.configured && !status.paired) {
        await _resumeDeviceLink();
      }
      widget.onMember?.call(status == null);
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _loading = false;
        _failed = true;
      });
      // Resolved, even if it failed: the other cards must not wait on this one forever.
      widget.onStatus(_status);
    }
  }

  /// A code link started earlier (the screen was closed and opened again) shows its code again.
  Future<void> _resumeDeviceLink() async {
    if (_device != null) return;
    try {
      final status = await widget.service.deviceLinkStatus();
      if (!mounted) return;
      // The server finished linking between reading the pairing above and this answer: show the link, not a start.
      if (status.state == CloudDeviceLinkState.linked) {
        setState(() => _justLinked = true);
        await _load(resume: false);
        return;
      }
      final link = status.link;
      if (status.state != CloudDeviceLinkState.waiting || link == null) {
        return;
      }
      setState(() => _device = link);
      _startPolling();
    } catch (_) {
      // Nothing to resume: the card offers a fresh start.
    }
  }

  /// "Link with BuoyAI Cloud": this server asks the Cloud for a short code and shows it with a QR code. The owner
  /// approves it on the Cloud page from any device, and the server notices by itself.
  Future<void> _startDeviceLink() async {
    final l10n = AppLocalizations.of(context);
    if (_busy || _cancelling) return;
    setState(() {
      _busy = true;
      _error = null;
      _linkError = null;
      _justLinked = false;
      _deviceEnd = null;
      _deviceMessage = null;
    });
    _graceTimer?.cancel();
    try {
      final link = await widget.service.startDeviceLink(_name.text.trim());
      if (!mounted) return;
      setState(() {
        _device = link;
        _showCode = false;
      });
      _startPolling();
    } catch (e) {
      if (!mounted) return;
      setState(() =>
          _error = apiErrorMessage(e, fallback: l10n.cloudLinkStartFailed));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  void _startPolling() {
    _poll?.cancel();
    _poll = Timer.periodic(_pollEvery, (_) => _pollNow());
  }

  Future<void> _pollNow() async {
    if (_polling || _device == null) return;
    _polling = true;
    try {
      final status = await widget.service.deviceLinkStatus();
      if (!mounted || _device == null) return;
      switch (status.state) {
        case CloudDeviceLinkState.waiting:
          break;
        case CloudDeviceLinkState.linked:
          _poll?.cancel();
          _graceTimer?.cancel();
          setState(() {
            _device = null;
            _justLinked = true;
          });
          await _load();
        case CloudDeviceLinkState.expired:
        case CloudDeviceLinkState.denied:
        case CloudDeviceLinkState.failed:
        case CloudDeviceLinkState.none:
          // "none": the server forgot the session (restarted, or cancelled elsewhere). Same as running out.
          _endDeviceLink(
              status.state == CloudDeviceLinkState.none
                  ? CloudDeviceLinkState.expired
                  : status.state,
              message: status.message);
      }
    } catch (_) {
      // A missed poll is not a failure: the next one asks again.
    } finally {
      _polling = false;
    }
  }

  /// The countdown ran out here, so ask the server what happened instead of assuming: a skewed clock here, or an
  /// approval in the last seconds, can still end in "linked". Only if the server stays unclear for a while does
  /// this give the code up.
  void _onCountdownEnded() {
    if (_device == null) return;
    _graceTimer?.cancel();
    _graceTimer = Timer(_expiryGrace, () {
      if (_device != null) _endDeviceLink(CloudDeviceLinkState.expired);
    });
    _pollNow();
  }

  void _endDeviceLink(CloudDeviceLinkState state, {String? message}) {
    _poll?.cancel();
    _graceTimer?.cancel();
    if (!mounted) return;
    setState(() {
      _device = null;
      _deviceEnd = state;
      _deviceMessage = message;
    });
  }

  /// Stops the code link. The code stays on screen, and polling goes on, until the server confirms: if the request
  /// does not get through, the server keeps waiting and could still pair, so Cancel must not look done.
  Future<void> _cancelDeviceLink() async {
    if (_cancelling || _device == null) return;
    final l10n = AppLocalizations.of(context);
    setState(() {
      _cancelling = true;
      _cancelError = null;
    });
    try {
      await widget.service.cancelDeviceLink();
      if (!mounted) return;
      _poll?.cancel();
      _graceTimer?.cancel();
      setState(() {
        _device = null;
        _deviceEnd = null;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() =>
          _cancelError = apiErrorMessage(e, fallback: l10n.cloudDeviceCancelFailed));
    } finally {
      if (mounted) setState(() => _cancelling = false);
    }
  }

  Future<void> _copyDeviceCode(String code) async {
    final l10n = AppLocalizations.of(context);
    await Clipboard.setData(ClipboardData(text: code));
    if (!mounted) return;
    showAppToast(context, l10n.cloudServersCopied, type: AppToastType.success);
  }

  Future<void> _pair() async {
    final l10n = AppLocalizations.of(context);
    final code = _code.text.trim();
    if (code.isEmpty || _busy) return;
    setState(() {
      _busy = true;
      _error = null;
      _linkError = null;
    });
    try {
      final status = await widget.service.pair(code, _name.text.trim());
      if (!mounted) return;
      _code.clear();
      setState(() => _setStatus(status));
    } catch (e) {
      if (!mounted) return;
      setState(
          () => _error = apiErrorMessage(e, fallback: l10n.cloudPairFailed));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _unlink() async {
    final l10n = AppLocalizations.of(context);
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text(l10n.cloudUnlinkTitle),
        content: Text(l10n.cloudUnlinkBody),
        actions: [
          TextButton(
              onPressed: () => Navigator.of(ctx).pop(false),
              child: Text(l10n.cloudCancel)),
          FilledButton(
            key: const ValueKey('cloud-confirm-unlink'),
            onPressed: () => Navigator.of(ctx).pop(true),
            child: Text(l10n.cloudUnlink),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;
    try {
      await widget.service.unpair();
      if (!mounted) return;
      _justLinked = false;
      showAppToast(context, l10n.cloudUnlinked, type: AppToastType.success);
      await _load();
    } catch (e) {
      if (!mounted) return;
      showAppToast(context, apiErrorMessage(e, fallback: l10n.cloudPairFailed),
          type: AppToastType.error);
    }
  }

  /// A short message for the older callback's reason. The reason itself is never shown.
  static String _linkErrorText(AppLocalizations l10n, String reason) =>
      switch (reason) {
        'cancelled' => l10n.cloudLinkErrorCancelled,
        'expired' => l10n.cloudLinkErrorExpired,
        _ => l10n.cloudLinkErrorFailed,
      };

  String _deviceEndText(AppLocalizations l10n, CloudDeviceLinkState state) =>
      switch (state) {
        CloudDeviceLinkState.expired => l10n.cloudDeviceExpired,
        CloudDeviceLinkState.denied => l10n.cloudDeviceDenied,
        _ => _deviceMessage ?? l10n.cloudLinkErrorFailed,
      };

  /// "cloud.buoymoney.com/link" for "https://cloud.buoymoney.com/link": what a person types.
  static String _shortAddress(String url) {
    final uri = Uri.tryParse(url);
    if (uri == null || uri.host.isEmpty) return url;
    final path = uri.path == '/' ? '' : uri.path;
    return '${uri.host}${uri.hasPort ? ':${uri.port}' : ''}$path';
  }

  Widget _deviceCodeView(AppLocalizations l10n, CloudDeviceLink device) {
    final address = _shortAddress(device.verificationUri);
    final host = Uri.tryParse(device.verificationUri)?.host ?? address;
    return Column(
      key: const ValueKey('cloud-device-link'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(l10n.cloudDeviceStep(address), style: cineBodyStyle()),
        const SizedBox(height: 14),
        Row(
          children: [
            Expanded(
              child: SelectableText(
                device.userCode,
                key: const ValueKey('cloud-device-code'),
                style: GoogleFonts.firaCode(
                    fontSize: 30,
                    fontWeight: FontWeight.w700,
                    letterSpacing: 4,
                    color: cineText),
              ),
            ),
            GlassTextAction(
              key: const ValueKey('cloud-device-copy'),
              onPressed: () => _copyDeviceCode(device.userCode),
              label: l10n.cloudServersCopy,
            ),
          ],
        ),
        const SizedBox(height: 14),
        Center(
          // A white quiet zone so phone cameras read it on the dark card.
          child: Container(
            padding: const EdgeInsets.all(10),
            decoration: BoxDecoration(
              color: Colors.white,
              borderRadius: BorderRadius.circular(12),
            ),
            child: QrImageView(
              key: const ValueKey('cloud-device-qr'),
              data: device.verificationUriComplete,
              size: 168,
              padding: EdgeInsets.zero,
              backgroundColor: Colors.white,
              semanticsLabel: l10n.cloudDeviceQrLabel,
            ),
          ),
        ),
        const SizedBox(height: 8),
        Text(l10n.cloudDeviceScan,
            textAlign: TextAlign.center, style: cineMutedStyle()),
        const SizedBox(height: 14),
        Center(
          child: GlassSecondaryButton(
            key: const ValueKey('cloud-device-open'),
            icon: Icons.open_in_new_rounded,
            onPressed: () => widget.open(device.verificationUriComplete),
            label: l10n.cloudDeviceOpen(host),
          ),
        ),
        const SizedBox(height: 16),
        Row(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            const SizedBox(
              width: 14,
              height: 14,
              child:
                  CircularProgressIndicator(strokeWidth: 2, color: cineAccent),
            ),
            const SizedBox(width: 10),
            Flexible(
              child: Text(l10n.cloudDeviceWaiting,
                  key: const ValueKey('cloud-device-waiting'),
                  style: cineBodyStyle()),
            ),
          ],
        ),
        if (device.expiresAt != null) ...[
          const SizedBox(height: 4),
          _ExpiryCountdown(
            key: ValueKey('cloud-device-expires-${device.userCode}'),
            expiresAt: device.expiresAt!,
            onExpired: _onCountdownEnded,
          ),
        ],
        if (_cancelError != null) ...[
          const SizedBox(height: 10),
          GlassNote(
              key: const ValueKey('cloud-device-cancel-error'),
              text: _cancelError!,
              tone: GlassNoteTone.error),
        ],
        Center(
          child: GlassTextAction(
            key: const ValueKey('cloud-device-cancel'),
            onPressed: _cancelling ? null : _cancelDeviceLink,
            label: l10n.cloudCancel,
            color: cineMuted,
          ),
        ),
      ],
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final status = _status;

    // Only the server administrator can read the pairing; a household member is told the owner manages it.
    if (!_loading && !_failed && status == null) {
      return _SectionCard(
        key: const ValueKey('cloud-owner-manages'),
        icon: Icons.link_rounded,
        title: l10n.cloudPairTitle,
        children: [
          GlassNote(
              text: l10n.ownerManagesThis,
              icon: Icons.admin_panel_settings_outlined),
        ],
      );
    }

    final unlinked =
        !_loading && !_failed && status!.configured && !status.paired;
    final device = _device;

    return _SectionCard(
      key: const ValueKey('cloud-pairing'),
      icon: Icons.link_rounded,
      title: l10n.cloudPairTitle,
      highlight: unlinked,
      children: [
        if (_loading)
          const GlassSpinner()
        else if (_failed)
          GlassNote(text: l10n.cloudLoadFailed, tone: GlassNoteTone.error)
        else if (!status!.configured)
          GlassNote(
              key: const ValueKey('cloud-not-configured'),
              text: l10n.cloudPairNotConfigured)
        else if (status.paired) ...[
          if (_justLinked) ...[
            GlassNote(
                key: const ValueKey('cloud-link-done'),
                text: l10n.cloudLinkDone,
                tone: GlassNoteTone.success),
            const SizedBox(height: 10),
          ],
          Row(
            children: [
              const Icon(Icons.check_circle_rounded,
                  size: 18, color: cineIncome),
              const SizedBox(width: 8),
              Expanded(
                child: Text(l10n.cloudPaired(status.name ?? ''),
                    key: const ValueKey('cloud-paired'),
                    style: cineRowTitleStyle()),
              ),
            ],
          ),
          if (status.pairedAt != null)
            Padding(
              padding: const EdgeInsets.only(left: 26, top: 2),
              child: Text(
                  l10n.cloudPairedSince(MaterialLocalizations.of(context)
                      .formatShortDate(status.pairedAt!)),
                  style: cineMutedStyle()),
            ),
          // Only after entering a code by hand: a server linked by code or in the browser was approved already.
          if (status.awaitingOwnerConfirmation) ...[
            const SizedBox(height: 10),
            GlassNote(
                key: const ValueKey('cloud-awaiting'),
                text: l10n.cloudPairAwaiting,
                tone: GlassNoteTone.warning),
          ],
          Align(
            alignment: Alignment.centerRight,
            child: GlassTextAction(
              key: const ValueKey('cloud-unlink'),
              onPressed: _unlink,
              label: l10n.cloudUnlink,
              color: cineMuted,
            ),
          ),
        ] else if (device != null)
          _deviceCodeView(l10n, device)
        else ...[
          if (_linkError != null) ...[
            GlassNote(
                key: const ValueKey('cloud-link-error'),
                text: _linkErrorText(l10n, _linkError!),
                tone: GlassNoteTone.error),
            const SizedBox(height: 10),
          ],
          if (_deviceEnd != null) ...[
            GlassNote(
                key: const ValueKey('cloud-device-ended'),
                text: _deviceEndText(l10n, _deviceEnd!),
                tone: _deviceEnd == CloudDeviceLinkState.expired
                    ? GlassNoteTone.warning
                    : GlassNoteTone.error),
            const SizedBox(height: 10),
          ],
          if (widget.bankNeedsLink) ...[
            GlassNote(
                key: const ValueKey('cloud-bank-needs-link'),
                icon: Icons.account_balance_outlined,
                text: l10n.cloudBankNeedsLink),
            const SizedBox(height: 12),
          ],
          Text(l10n.cloudLinkIntro, style: cineBodyStyle()),
          const SizedBox(height: 14),
          GlassPrimaryButton(
            key: const ValueKey('cloud-link-start'),
            expand: true,
            icon: Icons.link_rounded,
            busy: _busy && !_showCode,
            onPressed: _busy ? null : _startDeviceLink,
            label: _busy && !_showCode
                ? l10n.cloudLinkStarting
                : l10n.cloudLinkStart,
          ),
          if (_error != null) ...[
            const SizedBox(height: 10),
            GlassNote(
                key: const ValueKey('cloud-pair-error'),
                text: _error!,
                tone: GlassNoteTone.error),
          ],
          const SizedBox(height: 6),
          Center(
            child: GlassTextAction(
              key: const ValueKey('cloud-show-code'),
              onPressed:
                  _busy ? null : () => setState(() => _showCode = !_showCode),
              label: l10n.cloudLinkUseCode,
              color: cineMuted,
            ),
          ),
          if (_showCode) ...[
            const SizedBox(height: 6),
            Text(l10n.cloudPairIntro, style: cineMutedStyle()),
            const SizedBox(height: 12),
            GlassTextField(
              fieldKey: const ValueKey('cloud-code'),
              controller: _code,
              enabled: !_busy,
              autocorrect: false,
              textCapitalization: TextCapitalization.characters,
              label: l10n.cloudPairCodeLabel,
              onSubmitted: (_) => _pair(),
            ),
            const SizedBox(height: 10),
            GlassTextField(
              fieldKey: const ValueKey('cloud-name'),
              controller: _name,
              enabled: !_busy,
              label: l10n.cloudPairNameLabel,
            ),
            const SizedBox(height: 12),
            Align(
              alignment: Alignment.centerLeft,
              child: GlassSecondaryButton(
                key: const ValueKey('cloud-pair'),
                onPressed: _busy ? null : _pair,
                label: _busy ? l10n.cloudPairing : l10n.cloudPairButton,
              ),
            ),
          ],
        ],
      ],
    );
  }
}

/// "Code expires in 9:41", ticking each second. Calls [onExpired] once when it reaches zero.
class _ExpiryCountdown extends StatefulWidget {
  const _ExpiryCountdown(
      {super.key, required this.expiresAt, required this.onExpired});

  final DateTime expiresAt;
  final VoidCallback onExpired;

  @override
  State<_ExpiryCountdown> createState() => _ExpiryCountdownState();
}

class _ExpiryCountdownState extends State<_ExpiryCountdown> {
  Timer? _tick;
  bool _fired = false;

  @override
  void initState() {
    super.initState();
    _tick = Timer.periodic(const Duration(seconds: 1), (_) => _onTick());
  }

  @override
  void dispose() {
    _tick?.cancel();
    super.dispose();
  }

  Duration get _left {
    final left = widget.expiresAt.difference(DateTime.now());
    return left.isNegative ? Duration.zero : left;
  }

  void _onTick() {
    if (!mounted) return;
    if (_left == Duration.zero && !_fired) {
      _fired = true;
      _tick?.cancel();
      widget.onExpired();
      return;
    }
    setState(() {});
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final left = _left;
    final time =
        '${left.inMinutes}:${(left.inSeconds % 60).toString().padLeft(2, '0')}';
    return Text(l10n.cloudDeviceExpiresIn(time),
        key: const ValueKey('cloud-device-expires'),
        textAlign: TextAlign.center,
        style: cineMutedStyle());
  }
}

// ---------------------------------------------------------------- bank feed

class _BankSection extends StatefulWidget {
  const _BankSection(
      {required this.service,
      required this.open,
      required this.onNeedsLink,
      this.onLinkLost,
      this.paired,
      this.linkCardShown = false,
      this.member = false});

  final CloudService service;
  final Future<void> Function(String url) open;

  /// What the link card read (null when unknown, for example for someone who is not the administrator). When it
  /// changes, the section checks again whether the Cloud feed is ready.
  final bool? paired;

  /// The link card above offers "Link with BuoyAI Cloud", so an unlinked feed says nothing here.
  final bool linkCardShown;

  /// A household member: an unlinked feed says the owner manages it rather than asking them to link.
  final bool member;

  /// Tells the page that the Cloud bank feed is on but waits for the link.
  final ValueChanged<bool> onNeedsLink;

  /// The link was found gone while connecting a bank: the link card must read the pairing again.
  final VoidCallback? onLinkLost;

  @override
  State<_BankSection> createState() => _BankSectionState();
}

class _BankSectionState extends State<_BankSection> {
  bool _loading = true;
  bool _available = false;
  bool _linked = true;
  bool _failed = false;

  /// The providers answer was unreadable and the link card had not answered yet: read again when it does.
  bool _linkGuessed = false;
  List<BankConnectionSummary> _banks = const [];
  String? _pendingPage;
  bool _connecting = false;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void didUpdateWidget(covariant _BankSection oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.paired != widget.paired &&
        (oldWidget.paired != null || _linkGuessed)) {
      _load();
    }
  }

  void _setLinked(bool linked) {
    _linked = linked;
    widget.onNeedsLink(_available && !linked);
  }

  Future<void> _load() async {
    try {
      final available = await widget.service.bankFeedAvailable();
      if (!available) {
        if (!mounted) return;
        setState(() {
          _available = false;
          _loading = false;
          _setLinked(true);
        });
        return;
      }
      // The feed is on, but it can only connect a bank once this server is linked to the Cloud.
      // An unreadable answer is not a confirmed link: honor what the link card already knows (paired: false).
      final answer = await widget.service.bankFeedLinked();
      _linkGuessed = answer == null && widget.paired == null;
      final linked = answer ?? widget.paired ?? true;
      if (!linked) {
        if (!mounted) return;
        setState(() {
          _available = true;
          _loading = false;
          _failed = false;
          _setLinked(false);
        });
        return;
      }
      final banks = await widget.service.bankConnections();
      if (!mounted) return;
      setState(() {
        _available = true;
        _banks = banks;
        _loading = false;
        _failed = false;
        _setLinked(true);
      });
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _available = true;
        _loading = false;
        _failed = true;
      });
    }
  }

  Future<void> _connect() async {
    final l10n = AppLocalizations.of(context);
    if (_connecting) return;
    setState(() => _connecting = true);
    try {
      final page = await widget.service.startBankConnect();
      if (!mounted) return;
      if (page == null) {
        showAppToast(context, l10n.cloudBankConnectFailed,
            type: AppToastType.error);
        return;
      }
      setState(() => _pendingPage = page);
      await widget.open(page);
    } catch (e) {
      if (!mounted) return;
      // The link went away since the page loaded: that is the next step to show, not an error.
      if (await widget.service.bankFeedLinked() == false) {
        if (mounted) {
          setState(() => _setLinked(false));
          widget.onLinkLost?.call();
        }
        return;
      }
      if (!mounted) return;
      showAppToast(
          context, apiErrorMessage(e, fallback: l10n.cloudBankConnectFailed),
          type: AppToastType.error);
    } finally {
      if (mounted) setState(() => _connecting = false);
    }
  }

  Future<void> _finish() async {
    final l10n = AppLocalizations.of(context);
    final page = _pendingPage;
    if (page == null) return;
    try {
      final problem = await widget.service.finishBankConnect(page);
      if (!mounted) return;
      if (problem == null) {
        setState(() => _pendingPage = null);
        showAppToast(context, l10n.cloudBankConnected,
            type: AppToastType.success);
        await _load();
      } else {
        // Not finished yet is the expected answer when someone taps too early: say so in the card.
        showAppToast(
            context, problem.isEmpty ? l10n.cloudBankNotFinished : problem);
      }
    } catch (e) {
      if (!mounted) return;
      showAppToast(
          context, apiErrorMessage(e, fallback: l10n.cloudBankNotFinished),
          type: AppToastType.error);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    if (!_loading && !_available) return const SizedBox.shrink();
    // Not linked, and the link card above has the one action that fixes it: nothing to add here.
    if (!_loading && !_failed && !_linked && widget.linkCardShown) {
      return const SizedBox.shrink();
    }

    return _SectionCard(
      key: const ValueKey('cloud-bank'),
      icon: Icons.account_balance_outlined,
      title: l10n.cloudBankTitle,
      children: [
        Text(l10n.cloudBankIntro, style: cineBodyStyle()),
        const SizedBox(height: 12),
        if (_loading)
          const GlassSpinner()
        else if (_failed)
          GlassNote(text: l10n.cloudLoadFailed, tone: GlassNoteTone.error)
        else if (!_linked)
          GlassNote(
              key: const ValueKey('cloud-bank-not-linked'),
              text: widget.member ? l10n.ownerManagesThis : l10n.cloudBankNeedsLink)
        else ...[
          if (_banks.isEmpty)
            Text(l10n.cloudBankNone,
                key: const ValueKey('cloud-no-banks'),
                style: cineMutedStyle(size: 13)),
          for (final bank in _banks) _bankRow(context, bank),
          const SizedBox(height: 14),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              GlassPrimaryButton(
                key: const ValueKey('cloud-connect-bank'),
                onPressed: _connecting ? null : _connect,
                icon: Icons.add_rounded,
                label: l10n.cloudBankConnect,
              ),
              if (_pendingPage != null)
                GlassSecondaryButton(
                  key: const ValueKey('cloud-bank-done'),
                  onPressed: _finish,
                  label: l10n.cloudBankDone,
                ),
            ],
          ),
          if (_pendingPage != null) ...[
            const SizedBox(height: 8),
            Text(l10n.cloudBankOpened, style: cineMutedStyle()),
          ],
        ],
      ],
    );
  }

  Widget _bankRow(BuildContext context, BankConnectionSummary bank) {
    final l10n = AppLocalizations.of(context);
    final healthy = bank.isHealthy;
    final lastSync = bank.lastSyncAt == null
        ? l10n.cloudBankNeverSynced
        : l10n.cloudBankLastSync(MaterialLocalizations.of(context)
            .formatShortDate(bank.lastSyncAt!.toLocal()));

    final statusLine =
        '${healthy ? l10n.cloudBankStatusOk : l10n.cloudBankStatusAttention}, $lastSync';

    return Padding(
      key: ValueKey('cloud-bank-${bank.id}'),
      padding: const EdgeInsets.symmetric(vertical: 6),
      child: Row(
        children: [
          Container(
            padding: const EdgeInsets.all(8),
            decoration: BoxDecoration(
              color: cineAccent.withValues(alpha: 0.12),
              borderRadius: BorderRadius.circular(10),
            ),
            child: const Icon(Icons.account_balance_rounded,
                size: 18, color: cineAccent),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(bank.institutionName, style: cineRowTitleStyle()),
                Text(
                  statusLine,
                  style: GoogleFonts.sora(
                      fontSize: 12, color: healthy ? cineMuted : cineWarning),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

// ---------------------------------------------------------------- AI

class _AiSection extends StatefulWidget {
  const _AiSection({this.statusLoader, this.usageLoader});

  final Future<AiStatus> Function()? statusLoader;
  final Future<AiUsageSummary> Function()? usageLoader;

  @override
  State<_AiSection> createState() => _AiSectionState();
}

class _AiSectionState extends State<_AiSection> {
  bool _loading = true;
  bool _failed = false;
  AiStatus? _status;
  AiUsageSummary? _usage;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final status =
          await (widget.statusLoader ?? () => aiStatusService.fetch())();
      AiUsageSummary? usage;
      if (status.enabled) {
        try {
          usage =
              await (widget.usageLoader ?? () => AiUsageService().fetch())();
        } catch (_) {
          // The meter is optional; the status line still shows.
        }
      }
      if (!mounted) return;
      setState(() {
        _status = status;
        _usage = usage;
        _loading = false;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _loading = false;
        _failed = true;
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final status = _status;
    final usage = _usage;

    return _SectionCard(
      key: const ValueKey('cloud-ai'),
      icon: Icons.auto_awesome_rounded,
      title: l10n.cloudAiTitle,
      children: [
        if (_loading)
          const GlassSpinner()
        else if (_failed || status == null)
          GlassNote(text: l10n.cloudLoadFailed, tone: GlassNoteTone.error)
        else if (!status.enabled)
          Text(l10n.cloudAiOff,
              key: const ValueKey('cloud-ai-off'), style: cineBodyStyle())
        else ...[
          Text(
            l10n.cloudAiOn(status.provider == 'buoy-cloud'
                ? l10n.cloudAiProviderCloud
                : l10n.cloudAiProviderOwn),
            key: const ValueKey('cloud-ai-on'),
            style: cineBodyStyle(),
          ),
          if (usage != null) ...[
            const SizedBox(height: 12),
            if (!usage.unlimited)
              ClipRRect(
                borderRadius: BorderRadius.circular(6),
                child: LinearProgressIndicator(
                  key: const ValueKey('cloud-ai-meter'),
                  minHeight: 8,
                  value: (usage.percent.clamp(0, 100)) / 100,
                  backgroundColor: cineCardBorder,
                  color: usage.isOver
                      ? cineExpense
                      : usage.isWarning
                          ? cineWarning
                          : cineAccent,
                ),
              ),
            const SizedBox(height: 6),
            Text(
              usage.unlimited
                  ? l10n.cloudAiMeterUnlimited(formatMicros(usage.usedMicros))
                  : l10n.cloudAiMeter(formatMicros(usage.usedMicros),
                      formatMicros(usage.allowanceMicros)),
              key: const ValueKey('cloud-ai-used'),
              style: cineMutedStyle(),
            ),
            if (usage.isOver && usage.source == 'cloud') ...[
              const SizedBox(height: 8),
              GlassNote(
                  key: const ValueKey('cloud-ai-over'),
                  text: l10n.cloudAiOver,
                  tone: GlassNoteTone.warning),
            ] else if (usage.isWarning && usage.source == 'cloud') ...[
              const SizedBox(height: 6),
              Text(l10n.cloudAiWarning, style: cineMutedStyle()),
            ],
          ],
        ],
        const SizedBox(height: 14),
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: [
            GlassSecondaryButton(
              key: const ValueKey('cloud-ai-usage'),
              onPressed: () => context.push('/settings/ai-usage'),
              label: l10n.cloudAiManage,
            ),
            GlassSecondaryButton(
              key: const ValueKey('cloud-ai-settings'),
              onPressed: () => context.push('/settings/ai'),
              label: l10n.cloudAiSettings,
            ),
          ],
        ),
      ],
    );
  }
}

// ---------------------------------------------------------------- billing

/// A way into the billing screen: what you pay, line by line, and the next invoice. Both editions. A self-hosted
/// server that is not linked yet has no bill to show, so the card says so instead of opening an empty page.
class _BillingCard extends StatelessWidget {
  const _BillingCard(
      {this.notLinked = false,
      this.loading = false,
      this.portal = false,
      this.onReturn,
      this.ownerManages = false});

  final bool notLinked;

  /// Called when the person comes back from the billing page (its recovery action may have shown the link was lost).
  final VoidCallback? onReturn;

  /// The Cloud portal: its billing page is `/billing`.
  final bool portal;

  /// A household member: billing is the owner's, so the card says so instead of offering it.
  final bool ownerManages;

  /// The link state is not known yet (self-host): show a spinner rather than a button that may be wrong.
  final bool loading;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return _SectionCard(
      key: const ValueKey('cloud-billing-card'),
      icon: Icons.receipt_long_rounded,
      title: l10n.cloudBillTitle,
      children: [
        Text(l10n.cloudBillCardBody, style: cineBodyStyle()),
        const SizedBox(height: 12),
        if (loading)
          const GlassSpinner(key: ValueKey('cloud-billing-loading'))
        else if (ownerManages)
          GlassNote(
              key: const ValueKey('cloud-billing-owner-manages'),
              text: l10n.ownerManagesThis,
              icon: Icons.admin_panel_settings_outlined)
        else if (notLinked)
          GlassNote(
              key: const ValueKey('cloud-billing-not-linked'),
              text: l10n.cloudBillNotLinked)
        else
          Align(
            alignment: Alignment.centerLeft,
            child: GlassSecondaryButton(
              key: const ValueKey('cloud-open-billing'),
              onPressed: () async {
                await context
                    .push(portal ? '/billing' : '/settings/cloud/billing');
                onReturn?.call();
              },
              label: l10n.cloudBillOpen,
            ),
          ),
      ],
    );
  }
}

// ---------------------------------------------------------------- add-ons (hosted)

class _ProductsSection extends StatefulWidget {
  const _ProductsSection(
      {required this.service, required this.open, this.portal = false});

  final CloudService service;
  final Future<void> Function(String url) open;

  /// Bought in the Cloud portal: Stripe sends the customer back there, not into the hosted app.
  final bool portal;

  @override
  State<_ProductsSection> createState() => _ProductsSectionState();
}

class _ProductsSectionState extends State<_ProductsSection> {
  bool _loading = true;
  bool _failed = false;
  CloudCatalog? _catalog;
  CloudPlan _plan = const CloudPlan();
  final _selected = <String>{};
  String _interval = 'yearly';
  bool _busy = false;

  bool get _isIos => !kIsWeb && defaultTargetPlatform == TargetPlatform.iOS;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final catalog = await widget.service.catalog();
      var plan = const CloudPlan();
      if (catalog != null) {
        try {
          plan = await widget.service.plan();
        } catch (_) {
          // Without the plan every product simply reads as not held yet.
        }
      }
      if (!mounted) return;
      setState(() {
        _catalog = catalog;
        _plan = plan;
        _loading = false;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _loading = false;
        _failed = true;
      });
    }
  }

  CloudPrice? _price(CloudProduct p) =>
      _interval == 'monthly' ? p.monthly : p.yearly;

  /// What the selection costs, adding in anything a chosen product needs.
  Set<String> get _effective {
    final out = <String>{..._selected};
    final catalog = _catalog;
    if (catalog == null) return out;
    for (final name in _selected) {
      final product = catalog.products.where((p) => p.name == name).firstOrNull;
      if (product != null) {
        out.addAll(product.requires.where((r) => !_plan.holds(r)));
      }
    }
    return out;
  }

  int get _totalCents {
    final catalog = _catalog;
    if (catalog == null) return 0;
    var total = 0;
    for (final name in _effective) {
      final product = catalog.products.where((p) => p.name == name).firstOrNull;
      total += (product == null ? null : _price(product))?.amountCents ?? 0;
    }
    return total;
  }

  Future<void> _checkout() async {
    final l10n = AppLocalizations.of(context);
    if (_busy || _effective.isEmpty) return;
    setState(() => _busy = true);
    try {
      final platform = widget.portal
          ? 'portal'
          : kIsWeb
              ? 'web'
              : (defaultTargetPlatform == TargetPlatform.android
                  ? 'android'
                  : 'web');
      final url = await widget.service.checkout(
          interval: _interval,
          products: _effective.toList(),
          platform: platform);
      if (url.isEmpty) throw StateError('no checkout address');
      await widget.open(url);
    } catch (e) {
      if (!mounted) return;
      showAppToast(context,
          apiErrorMessage(e, fallback: l10n.cloudProductsCheckoutFailed),
          type: AppToastType.error);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  String _money(int cents) => formatCurrencyCode(cents / 100, 'USD');

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final catalog = _catalog;

    // A server with no Cloud billing has no catalog to show.
    if (!_loading && !_failed && catalog == null) {
      return const SizedBox.shrink();
    }

    return _SectionCard(
      key: const ValueKey('cloud-products'),
      icon: Icons.add_shopping_cart_rounded,
      title: l10n.cloudProductsTitle,
      children: [
        if (_loading)
          const GlassSpinner()
        else if (_failed)
          GlassNote(text: l10n.cloudLoadFailed, tone: GlassNoteTone.error)
        else ...[
          Text(
            catalog!.founding
                ? l10n.cloudProductsFounding
                : l10n.cloudProductsList,
            key: ValueKey(
                catalog.founding ? 'cloud-founding' : 'cloud-list-prices'),
            style: cineBodyStyle(),
          ),
          const SizedBox(height: 8),
          if (catalog.products.isEmpty)
            Text(l10n.cloudProductsEmpty, style: cineMutedStyle(size: 13)),
          for (final product in catalog.products) _productRow(context, product),
          if (_plan.hasSubscription) ...[
            const SizedBox(height: 8),
            Text(l10n.cloudProductsHasPlan,
                key: const ValueKey('cloud-has-plan'), style: cineMutedStyle()),
            Align(
              alignment: Alignment.centerLeft,
              child: TextButton(
                key: const ValueKey('cloud-billing'),
                onPressed: () => context.push('/billing'),
                child: Text(l10n.cloudProductsBilling),
              ),
            ),
          ] else if (_isIos) ...[
            const SizedBox(height: 8),
            Text(l10n.cloudProductsIos,
                key: const ValueKey('cloud-ios-note'), style: cineMutedStyle()),
          ] else if (catalog.products.isNotEmpty) ...[
            const SizedBox(height: 8),
            SegmentedButton<String>(
              key: const ValueKey('cloud-interval'),
              segments: [
                ButtonSegment(
                    value: 'monthly', label: Text(l10n.cloudProductsMonthly)),
                ButtonSegment(
                    value: 'yearly', label: Text(l10n.cloudProductsYearly)),
              ],
              selected: {_interval},
              onSelectionChanged: (v) => setState(() => _interval = v.first),
            ),
            const SizedBox(height: 8),
            if (_effective.isNotEmpty)
              Text(
                l10n.cloudProductsTotal(
                    _money(_totalCents),
                    _interval == 'monthly'
                        ? l10n.cloudProductsPerMonth
                        : l10n.cloudProductsPerYear),
                key: const ValueKey('cloud-total'),
                style: theme.textTheme.titleSmall,
              ),
            const SizedBox(height: 8),
            GlassPrimaryButton(
              key: const ValueKey('cloud-checkout'),
              expand: true,
              onPressed: (_busy || _effective.isEmpty) ? null : _checkout,
              label: l10n.cloudProductsCheckout,
            ),
          ],
        ],
      ],
    );
  }

  Widget _productRow(BuildContext context, CloudProduct product) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final held = _plan.holds(product.name);
    final monthly =
        product.monthly == null ? '' : _money(product.monthly!.amountCents);
    final yearly =
        product.yearly == null ? '' : _money(product.yearly!.amountCents);
    final missing = product.requires.where((r) => !_plan.holds(r)).toList();
    final details = <String>[
      if (product.includedLogins != null)
        l10n.cloudProductLogins(product.includedLogins!),
      if (missing.isNotEmpty)
        l10n.cloudProductsRequires(_catalog?.products
                .where((p) => p.name == missing.first)
                .firstOrNull
                ?.displayName ??
            missing.first),
    ];

    return CheckboxListTile(
      key: ValueKey('cloud-product-${product.name}'),
      contentPadding: EdgeInsets.zero,
      controlAffinity: ListTileControlAffinity.leading,
      value: held || _selected.contains(product.name),
      onChanged: (held || _plan.hasSubscription || _isIos)
          ? null
          : (v) => setState(() => v == true
              ? _selected.add(product.name)
              : _selected.remove(product.name)),
      title: Row(
        children: [
          Expanded(
              child: Text(product.displayName,
                  style: theme.textTheme.titleSmall
                      ?.copyWith(fontWeight: FontWeight.w700))),
          if (held)
            Text(l10n.cloudProductsOnPlan,
                style: theme.textTheme.labelMedium
                    ?.copyWith(color: theme.colorScheme.primary)),
        ],
      ),
      subtitle: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (monthly.isNotEmpty && yearly.isNotEmpty)
            Text(l10n.cloudProductPrice(monthly, yearly),
                style: theme.textTheme.bodySmall),
          if (details.isNotEmpty)
            Text(details.join(', '), style: cineMutedStyle()),
        ],
      ),
    );
  }
}

// ---------------------------------------------------------------- your servers (hosted)

/// The hosted app is the Cloud portal: the user's own self-hosted servers, a pairing code to link one, and the
/// confirm and revoke actions. Making a code and confirming a server need the step-up second factor.
class _ServersSection extends StatefulWidget {
  const _ServersSection({required this.service, required this.stepUp});

  final CloudService service;

  /// Runs the step-up dialog. True when the user passed it.
  final Future<bool> Function(BuildContext context) stepUp;

  @override
  State<_ServersSection> createState() => _ServersSectionState();
}

class _ServersSectionState extends State<_ServersSection> {
  bool _loading = true;
  bool _failed = false;
  bool _available = true;
  bool _noAccount = false;
  bool _needsEmail = false;
  bool _busy = false;
  List<CloudServer> _servers = const [];
  CloudPairingCode? _code;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final servers = await widget.service.cloudServers();
      if (!mounted) return;
      setState(() {
        _available = servers != null;
        _servers = servers ?? const [];
        _noAccount = false;
        _failed = false;
        _loading = false;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _noAccount = _isNoAccount(e);
        _failed = !_noAccount;
        _loading = false;
      });
    }
  }

  static bool _isNoAccount(Object e) =>
      CloudService.statusOf(e) == 409 &&
      CloudService.codeOf(e) == 'no_cloud_account';

  /// Runs [action]. When the server asks for the second factor, runs the step-up dialog and tries once more.
  /// Returns false when the user stopped or the account's email is not confirmed yet (then [_needsEmail] is set).
  Future<bool> _withStepUp(Future<void> Function() action) async {
    try {
      await action();
      return true;
    } catch (e) {
      if (CloudService.needsVerifiedEmail(e)) {
        // Settings shows the confirm-email banner; the step-up can only send its code once that is done.
        if (mounted) setState(() => _needsEmail = true);
        return false;
      }
      if (CloudService.statusOf(e) == 403 &&
          CloudService.errorOf(e) == 'MfaRequired') {
        if (!mounted || !await widget.stepUp(context)) return false;
        await action();
        return true;
      }
      rethrow;
    }
  }

  void _showError(Object e) {
    if (!mounted) return;
    if (_isNoAccount(e)) {
      setState(() => _noAccount = true);
      return;
    }
    final l10n = AppLocalizations.of(context);
    showAppToast(context, apiErrorMessage(e, fallback: l10n.cloudServersFailed),
        type: AppToastType.error);
  }

  Future<void> _makeCode() async {
    if (_busy) return;
    setState(() {
      _busy = true;
      _needsEmail = false;
    });
    try {
      CloudPairingCode? code;
      final done = await _withStepUp(
          () async => code = await widget.service.createPairingCode());
      if (!mounted || !done) return;
      setState(() => _code = code);
    } catch (e) {
      _showError(e);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _copy(String code) async {
    final l10n = AppLocalizations.of(context);
    await Clipboard.setData(ClipboardData(text: code));
    if (!mounted) return;
    showAppToast(context, l10n.cloudServersCopied, type: AppToastType.success);
  }

  Future<void> _confirm(CloudServer server) async {
    if (_busy) return;
    final l10n = AppLocalizations.of(context);
    setState(() {
      _busy = true;
      _needsEmail = false;
    });
    try {
      final done =
          await _withStepUp(() => widget.service.confirmServer(server.id));
      if (!mounted || !done) return;
      showAppToast(context, l10n.cloudServersConfirmed,
          type: AppToastType.success);
      await _load();
    } catch (e) {
      _showError(e);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _revoke(CloudServer server) async {
    if (_busy) return;
    final l10n = AppLocalizations.of(context);
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text(l10n.cloudServersRevokeTitle(server.name)),
        content: Text(l10n.cloudServersRevokeBody),
        actions: [
          TextButton(
              onPressed: () => Navigator.of(ctx).pop(false),
              child: Text(l10n.cloudCancel)),
          FilledButton(
            key: const ValueKey('cloud-server-confirm-revoke'),
            onPressed: () => Navigator.of(ctx).pop(true),
            child: Text(l10n.cloudServersRevoke),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;
    setState(() => _busy = true);
    try {
      await widget.service.revokeServer(server.id);
      if (!mounted) return;
      showAppToast(context, l10n.cloudServersRevoked,
          type: AppToastType.success);
      await _load();
    } catch (e) {
      _showError(e);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    // No Cloud portal on this server (404): nothing to show.
    if (!_loading && !_failed && !_available) return const SizedBox.shrink();

    return _SectionCard(
      key: const ValueKey('cloud-servers'),
      icon: Icons.dns_outlined,
      title: l10n.cloudServersTitle,
      children: [
        Text(l10n.cloudServersIntro, style: cineBodyStyle()),
        const SizedBox(height: 12),
        if (_loading)
          const GlassSpinner()
        else if (_failed)
          GlassNote(text: l10n.cloudLoadFailed, tone: GlassNoteTone.error)
        else if (_noAccount)
          Text(l10n.cloudServersNoAccount,
              key: const ValueKey('cloud-servers-no-account'),
              style: cineBodyStyle())
        else ...[
          if (_code != null) _codeBox(context, _code!),
          if (_needsEmail) ...[
            Text(l10n.cloudServersNeedsEmail,
                key: const ValueKey('cloud-servers-needs-email'),
                style: cineBodyStyle(color: cineWarning)),
            const SizedBox(height: 8),
          ],
          Align(
            alignment: Alignment.centerLeft,
            child: GlassPrimaryButton(
              key: const ValueKey('cloud-servers-make-code'),
              onPressed: _busy ? null : _makeCode,
              label:
                  _busy ? l10n.cloudServersMaking : l10n.cloudServersMakeCode,
            ),
          ),
          const SizedBox(height: 12),
          if (_servers.isEmpty)
            Text(l10n.cloudServersEmpty,
                key: const ValueKey('cloud-servers-empty'),
                style: cineMutedStyle(size: 13))
          else
            for (final server in _servers) _serverRow(context, server),
        ],
      ],
    );
  }

  Widget _codeBox(BuildContext context, CloudPairingCode code) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final expires = code.expiresAt;
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: SelectableText(
                  code.code,
                  key: const ValueKey('cloud-servers-code'),
                  style: theme.textTheme.headlineMedium?.copyWith(
                      fontFamily: 'monospace',
                      fontWeight: FontWeight.w700,
                      letterSpacing: 2),
                ),
              ),
              TextButton.icon(
                key: const ValueKey('cloud-servers-copy'),
                onPressed: () => _copy(code.code),
                icon: const Icon(Icons.copy, size: 18),
                label: Text(l10n.cloudServersCopy),
              ),
            ],
          ),
          if (expires != null)
            Text(
                l10n.cloudServersCodeExpires(MaterialLocalizations.of(context)
                    .formatTimeOfDay(TimeOfDay.fromDateTime(expires))),
                key: const ValueKey('cloud-servers-code-expires'),
                style: cineMutedStyle()),
          Text(l10n.cloudServersCodeHelp, style: cineMutedStyle()),
        ],
      ),
    );
  }

  Widget _serverRow(BuildContext context, CloudServer server) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final muted = theme.colorScheme.onSurface.withValues(alpha: 0.6);
    final status = server.revoked
        ? l10n.cloudServersStatusRevoked
        : server.awaitingConfirmation
            ? l10n.cloudServersStatusAwaiting
            : l10n.cloudServersStatusLinked;
    final statusColor = server.revoked
        ? muted
        : server.awaitingConfirmation
            ? theme.colorScheme.tertiary
            : theme.colorScheme.primary;
    final details = <String>[
      if (server.serverVersion != null)
        l10n.cloudServersVersion(server.serverVersion!),
      if (server.pairedAt != null)
        l10n.cloudServersPaired(MaterialLocalizations.of(context)
            .formatShortDate(server.pairedAt!)),
    ];

    return Padding(
      key: ValueKey('cloud-server-${server.id}'),
      padding: const EdgeInsets.symmetric(vertical: 6),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(server.name,
                    style: theme.textTheme.titleSmall
                        ?.copyWith(fontWeight: FontWeight.w700)),
                Text(status,
                    key: ValueKey('cloud-server-status-${server.id}'),
                    style: theme.textTheme.bodySmall
                        ?.copyWith(color: statusColor)),
                if (details.isNotEmpty)
                  Text(details.join(', '), style: cineMutedStyle()),
              ],
            ),
          ),
          if (!server.revoked) ...[
            if (server.awaitingConfirmation)
              TextButton(
                key: ValueKey('cloud-server-confirm-${server.id}'),
                onPressed: _busy ? null : () => _confirm(server),
                child: Text(l10n.cloudServersConfirm),
              ),
            TextButton(
              key: ValueKey('cloud-server-revoke-${server.id}'),
              onPressed: _busy ? null : () => _revoke(server),
              child: Text(l10n.cloudServersRevoke),
            ),
          ],
        ],
      ),
    );
  }
}
