import 'package:flutter/foundation.dart' show defaultTargetPlatform, kIsWeb, TargetPlatform;
import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:provider/provider.dart';
import '../l10n/generated/app_localizations.dart';
import '../models/receipt_models.dart';
import '../providers/auth_provider.dart';
import '../services/receipts_service.dart';
import '../utils/formatters.dart';
import '../widgets/ai_consent_sheet.dart';
import '../widgets/app_toast.dart';
import '../widgets/cinematic/glass_settings.dart';
import '../widgets/receipt_scan_sheet.dart';

/// Receipts: send BuoyAI a receipt (a screenshot of an order page, a photo of a paper receipt)
/// and apply the split it offers for the matching bank charge. Also the list of receipts sent,
/// including ones still waiting for their charge.
class ReceiptsPage extends StatefulWidget {
  const ReceiptsPage({super.key, this.service});

  /// Injected in tests; the app uses the shared instance.
  final ReceiptsService? service;

  @override
  State<ReceiptsPage> createState() => _ReceiptsPageState();
}

class _ReceiptsPageState extends State<ReceiptsPage> {
  ReceiptScanStatusInfo? _status;
  List<ReceiptScan> _scans = [];
  bool _loading = true;
  bool _failed = false;
  final Set<int> _busy = {};

  ReceiptsService get _service => widget.service ?? receiptsService;

  bool get _android => !kIsWeb && defaultTargetPlatform == TargetPlatform.android;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load({bool quiet = false}) async {
    if (!quiet) {
      setState(() {
        _loading = true;
        _failed = false;
      });
    }
    try {
      final status = await _service.status();
      final scans = status.blocker == ReceiptBlocker.none ? await _service.list() : <ReceiptScan>[];
      if (!mounted) return;
      setState(() {
        _status = status;
        _scans = scans;
        _loading = false;
      });
    } catch (_) {
      if (!mounted || quiet) return;
      setState(() {
        _failed = true;
        _loading = false;
      });
    }
  }

  Future<void> _scan() async {
    await pickAndScanReceipt(context, service: widget.service);
    if (mounted) await _load(quiet: true);
  }

  Future<void> _turnOnAi() async {
    await showAiConsentSheet(context);
    if (!mounted) return;
    await context.read<AuthProvider?>()?.refreshUser();
    await _load(quiet: true);
  }

  Future<void> _apply(ReceiptScan r) async {
    final l10n = AppLocalizations.of(context);
    setState(() => _busy.add(r.id));
    try {
      final updated = await _service.apply(r.id);
      if (!mounted) return;
      showAppToast(
        context,
        updated.status == ReceiptStatus.applied ? l10n.rcptApplied : (updated.message ?? l10n.rcptActionFailed),
        type: updated.status == ReceiptStatus.applied ? AppToastType.success : AppToastType.error,
      );
      await _load(quiet: true);
    } catch (_) {
      if (mounted) showAppToast(context, l10n.rcptActionFailed, type: AppToastType.error);
    } finally {
      if (mounted) setState(() => _busy.remove(r.id));
    }
  }

  Future<void> _dismiss(ReceiptScan r) async {
    setState(() => _busy.add(r.id));
    try {
      await _service.dismiss(r.id);
      await _load(quiet: true);
    } catch (_) {
      if (mounted) showAppToast(context, AppLocalizations.of(context).rcptActionFailed, type: AppToastType.error);
    } finally {
      if (mounted) setState(() => _busy.remove(r.id));
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final List<Widget> children;
    if (_loading) {
      children = const [GlassSpinner()];
    } else if (_failed || _status == null) {
      children = [GlassLoadError(message: l10n.rcptLoadError, retryLabel: l10n.rcptRetry, onRetry: _load)];
    } else {
      children = _main(_status!, l10n);
    }
    return GlassSubPage(
      title: l10n.rcptTitle,
      intro: l10n.rcptIntro,
      onRefresh: (_loading || _failed || _status == null) ? null : _load,
      children: children,
    );
  }

  List<Widget> _main(ReceiptScanStatusInfo status, AppLocalizations l10n) {
    switch (status.blocker) {
      case ReceiptBlocker.premiumRequired:
        return [
          GlassSection(
            icon: Icons.workspace_premium_outlined,
            title: l10n.rcptNeedsPremium,
            highlight: true,
            children: [
              GlassPrimaryButton(onPressed: () => context.push('/billing'), label: l10n.rcptSeePremium),
            ],
          ),
        ];
      case ReceiptBlocker.aiConsentRequired:
        return [
          GlassSection(
            icon: Icons.auto_awesome_outlined,
            title: l10n.rcptNeedsAi,
            highlight: true,
            children: [GlassPrimaryButton(onPressed: _turnOnAi, label: l10n.rcptTurnOnAi)],
          ),
        ];
      case ReceiptBlocker.unavailable:
      case ReceiptBlocker.dailyLimit:
      case ReceiptBlocker.none:
        break;
    }
    return [
      GlassSection(
        icon: Icons.document_scanner_outlined,
        title: l10n.rcptHowTitle,
        highlight: true,
        children: [
          _step(1, l10n.rcptHowStep1),
          _step(2, _android ? l10n.rcptHowStep2Share : l10n.rcptHowStep2Upload),
          _step(3, l10n.rcptHowStep3),
          const SizedBox(height: 4),
          GlassPrimaryButton(
            key: const Key('rcpt-scan'),
            onPressed: _scan,
            icon: Icons.document_scanner_outlined,
            label: l10n.rcptScanButton,
            expand: true,
          ),
          const SizedBox(height: 14),
          GlassNote(text: l10n.rcptPrivacy, icon: Icons.lock_outline_rounded),
        ],
      ),
      GlassSection(
        icon: Icons.receipt_long_outlined,
        title: l10n.rcptRecent,
        children: [
          if (_scans.isEmpty)
            Text(l10n.rcptNone, style: cineMutedStyle(size: 13))
          else
            for (final (i, r) in _scans.indexed) ...[
              if (i > 0) const GlassDivider(),
              _tile(r, l10n),
            ],
        ],
      ),
    ];
  }

  Widget _tile(ReceiptScan r, AppLocalizations l10n) {
    final busy = _busy.contains(r.id);
    final title = [r.retailer ?? l10n.rcptUnknownStore, if (r.total != null) formatCurrency(r.total!)].join('  ');
    final ready = r.status == ReceiptStatus.ready;
    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      Row(children: [
        Expanded(child: Text(title, style: cineRowTitleStyle())),
        const SizedBox(width: 8),
        GlassStatusPill(label: _statusLabel(r, l10n), color: _statusColor(r.status)),
      ]),
      if (r.message != null && !ready) ...[
        const SizedBox(height: 6),
        Text(r.message!, style: cineMutedStyle()),
      ],
      if (ready) ...[
        const SizedBox(height: 10),
        for (final s in r.charges.expand((c) => c.splits))
          Padding(
            padding: const EdgeInsets.only(bottom: 6),
            child: Row(children: [
              Expanded(
                child: Text(
                  s.items.isEmpty
                      ? (s.categoryName ?? l10n.rcptCategoryFallback)
                      : '${s.categoryName ?? l10n.rcptCategoryFallback}: ${s.items.join(', ')}',
                  maxLines: 2,
                  overflow: TextOverflow.ellipsis,
                  style: cineBodyStyle(),
                ),
              ),
              const SizedBox(width: 8),
              Text(formatCurrency(s.amount), style: GoogleFonts.firaCode(fontSize: 13, color: cineText)),
            ]),
          ),
        const SizedBox(height: 6),
        Row(children: [
          GlassPrimaryButton(onPressed: busy ? null : () => _apply(r), busy: busy, label: l10n.rcptApply),
          const SizedBox(width: 8),
          GlassTextAction(onPressed: busy ? null : () => _dismiss(r), label: l10n.rcptDismiss, color: cineMuted),
        ]),
      ],
    ]);
  }

  static Color _statusColor(ReceiptStatus s) => switch (s) {
        ReceiptStatus.ready => cineAccent,
        ReceiptStatus.applied => cineIncome,
        ReceiptStatus.unmatched => cineWarning,
        _ => cineMuted,
      };

  String _statusLabel(ReceiptScan r, AppLocalizations l10n) => switch (r.status) {
        ReceiptStatus.ready => l10n.rcptStatusReady(r.categoryCount),
        ReceiptStatus.applied => l10n.rcptStatusApplied,
        ReceiptStatus.unmatched => l10n.rcptStatusWaiting,
        ReceiptStatus.dismissed => l10n.rcptStatusDismissed,
        ReceiptStatus.notAReceipt => l10n.rcptStatusNotReceipt,
        ReceiptStatus.expired => l10n.rcptStatusExpired,
        ReceiptStatus.unknown => l10n.rcptStatusUnknown,
      };

  Widget _step(int n, String text) => Padding(
        padding: const EdgeInsets.only(bottom: 12),
        child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Container(
            width: 24,
            height: 24,
            alignment: Alignment.center,
            decoration: BoxDecoration(
              shape: BoxShape.circle,
              color: cineAccent.withValues(alpha: 0.15),
              border: Border.all(color: cineAccent.withValues(alpha: 0.4)),
            ),
            child: Text(n.toString(),
                style: GoogleFonts.firaCode(fontSize: 12, fontWeight: FontWeight.w600, color: cineAccent)),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Padding(
              padding: const EdgeInsets.only(top: 2),
              child: Text(text, style: cineBodyStyle()),
            ),
          ),
        ]),
      );
}
