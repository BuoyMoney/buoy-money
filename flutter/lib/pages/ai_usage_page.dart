import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:provider/provider.dart';

import '../l10n/generated/app_localizations.dart';
import '../models/billing_models.dart';
import '../providers/auth_provider.dart';
import '../services/ai_usage_service.dart';
import '../utils/formatters.dart';
import '../widgets/cinematic/glass_settings.dart';

/// How much of the AI allowance is used this period, where it went, and (for the server administrator, when AI comes
/// from BuoyAI Cloud) the pay-as-you-go switch.
///
/// The numbers are the server's own account: BuoyAI Cloud's meter when the allowance lives there, otherwise this
/// server's usage rows. The page never does money arithmetic with floats; amounts are whole micro-dollars.
class AiUsagePage extends StatefulWidget {
  const AiUsagePage({super.key, this.service, this.isAdmin});

  /// Test seam.
  final AiUsageService? service;

  /// Test seam. Null reads the signed-in user's tier.
  final bool? isAdmin;

  @override
  State<AiUsagePage> createState() => _AiUsagePageState();
}

class _AiUsagePageState extends State<AiUsagePage> {
  late final AiUsageService _service = widget.service ?? aiUsageService;
  AiUsageSummary? _summary;
  String? _error;
  bool _loading = true;

  final _capController = TextEditingController();
  bool _saving = false;
  String? _paygError;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _capController.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final summary = await _service.fetch();
      if (!mounted) return;
      setState(() {
        _summary = summary;
        _loading = false;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _error = AppLocalizations.of(context).aiUsageLoadFailed;
        _loading = false;
      });
    }
  }

  bool _isAdmin(BuildContext context) {
    final override = widget.isAdmin;
    if (override != null) return override;
    final user = context.read<AuthProvider>().user;
    return user != null && SubscriptionTierExtension.fromString(user.subscriptionTier) == SubscriptionTier.admin;
  }

  Future<void> _setPayg(bool enabled) async {
    final l10n = AppLocalizations.of(context);
    final current = _summary!;
    var capMicros = 0;
    if (enabled) {
      final parsed = parseDollarsToMicros(_capController.text);
      final max = current.payg.maxCapMicros;
      if (parsed == null || parsed <= 0 || (max > 0 && parsed > max)) {
        setState(() => _paygError = l10n.aiUsagePaygBadLimit(formatMicros(max)));
        return;
      }
      capMicros = parsed;
    }
    setState(() {
      _saving = true;
      _paygError = null;
    });
    try {
      final updated = await _service.setPayg(enabled: enabled, capMicros: capMicros);
      if (!mounted) return;
      setState(() {
        _summary = updated;
        _saving = false;
      });
    } on DioException catch (e) {
      if (!mounted) return;
      final data = e.response?.data;
      final message = data is Map && data['message'] is String ? data['message'] as String : null;
      setState(() {
        _paygError = message ?? l10n.aiUsagePaygSaveFailed;
        _saving = false;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _paygError = l10n.aiUsagePaygSaveFailed;
        _saving = false;
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return GlassSubPage(
      title: l10n.aiUsageTitle,
      children: _loading
          ? const [GlassSpinner()]
          : _error != null
              ? [GlassLoadError(message: _error!, retryLabel: l10n.aiSetRetry, onRetry: _load)]
              : _buildBody(context, _summary!),
    );
  }

  List<Widget> _buildBody(BuildContext context, AiUsageSummary s) {
    final l10n = AppLocalizations.of(context);
    final loc = MaterialLocalizations.of(context);
    final period = l10n.aiUsagePeriod(loc.formatShortDate(s.periodStart), loc.formatShortDate(s.periodEnd));
    final big = GoogleFonts.firaCode(fontSize: 24, fontWeight: FontWeight.w600, color: cineText);
    return [
      GlassSection(
        icon: Icons.data_usage_rounded,
        title: period,
        highlight: true,
        children: [
          if (s.unlimited) ...[
            Text(l10n.aiUsageSpent(formatMicros(s.usedMicros)), style: big),
            const SizedBox(height: 12),
            Text(l10n.aiUsageUnlimitedTitle, style: cineRowTitleStyle()),
            const SizedBox(height: 3),
            Text(l10n.aiUsageUnlimitedBody, style: cineMutedStyle()),
          ] else ...[
            Text(l10n.aiUsageOfAllowance(formatMicros(s.usedMicros), formatMicros(s.allowanceMicros)), style: big),
            const SizedBox(height: 14),
            Semantics(
              label: l10n.aiUsagePercentUsed(s.percent),
              child: _Bar(
                value: (s.percent / 100).clamp(0.0, 1.0),
                height: 10,
                color: s.isOver
                    ? cineExpense
                    : s.isWarning
                        ? cineWarning
                        : cineAccent,
              ),
            ),
            const SizedBox(height: 8),
            Text(l10n.aiUsagePercentUsed(s.percent), style: cineMutedStyle()),
            if (_notice(l10n, s) case final notice?) ...[
              const SizedBox(height: 12),
              GlassNote(
                text: notice,
                tone: s.isOver && s.source == 'cloud' ? GlassNoteTone.error : GlassNoteTone.warning,
              ),
            ],
            if (s.projectedMicros > s.usedMicros && s.projectedMicros > s.allowanceMicros) ...[
              const SizedBox(height: 10),
              Text(l10n.aiUsageProjected(formatMicros(s.projectedMicros)), style: cineMutedStyle()),
            ],
          ],
        ],
      ),
      if (s.source == 'cloud') _buildPaygCard(context, s),
      GlassSection(
        icon: Icons.pie_chart_outline_rounded,
        title: l10n.aiUsageByFeature,
        children: [
          if (s.byFeature.isEmpty)
            Text(l10n.aiUsageNoCalls, style: cineMutedStyle(size: 13))
          else
            for (final f in s.byFeature) _FeatureRow(usage: f, total: s.usedMicros, label: _featureName(l10n, f.feature)),
        ],
      ),
    ];
  }

  String? _notice(AppLocalizations l10n, AiUsageSummary s) {
    if (s.blocked) return l10n.aiUsageBlocked;
    // Only BuoyAI Cloud's allowance actually stops anything; a server's own monthly figure is a warning, never "used up".
    if (s.isOver && s.source == 'cloud') return s.payg.enabled ? l10n.aiUsageOverPayg : l10n.aiUsageOver;
    if (s.isOver || s.isWarning) return l10n.aiUsageWarning(s.percent);
    return null;
  }

  Widget _buildPaygCard(BuildContext context, AiUsageSummary s) {
    final l10n = AppLocalizations.of(context);
    final admin = _isAdmin(context);
    final payg = s.payg;
    return GlassSection(
      icon: Icons.bolt_rounded,
      title: l10n.aiUsagePaygTitle,
      children: [
        if (!payg.available)
          GlassNote(text: l10n.aiUsagePaygUnavailable)
        else if (payg.enabled)
          Text(l10n.aiUsagePaygOnBody(formatMicros(payg.capMicros), formatMicros(payg.usedMicros)), style: cineBodyStyle())
        else
          Text(l10n.aiUsagePaygOffBody, style: cineBodyStyle()),
        if (payg.available && !admin) ...[
          const SizedBox(height: 10),
          Text(l10n.ownerManagesThis, style: cineMutedStyle()),
        ],
        if (payg.available && admin) ...[
          const SizedBox(height: 14),
          if (!payg.enabled)
            GlassTextField(
              controller: _capController,
              keyboardType: const TextInputType.numberWithOptions(decimal: true),
              label: l10n.aiUsagePaygLimitLabel,
              prefixText: '${currencySymbol('USD')} ',
            ),
          if (_paygError != null) ...[
            const SizedBox(height: 10),
            GlassNote(text: _paygError!, tone: GlassNoteTone.error),
          ],
          const SizedBox(height: 14),
          if (payg.enabled)
            GlassSecondaryButton(
              onPressed: _saving ? null : () => _setPayg(false),
              label: l10n.aiUsagePaygTurnOff,
            )
          else
            GlassPrimaryButton(
              onPressed: _saving ? null : () => _setPayg(true),
              busy: _saving,
              label: l10n.aiUsagePaygTurnOn,
            ),
        ],
      ],
    );
  }

  static String _featureName(AppLocalizations l10n, String feature) => switch (feature) {
        'chat' => l10n.aiUsageFeatChat,
        'categorization' => l10n.aiUsageFeatCategorization,
        'insights' => l10n.aiUsageFeatInsights,
        'agentic' => l10n.aiUsageFeatAgentic,
        'memory-extraction' => l10n.aiUsageFeatMemory,
        'subscriptions' => l10n.aiUsageFeatSubscriptions,
        'receipts' => l10n.aiUsageFeatReceipts,
        'wrapped' => l10n.aiUsageFeatWrapped,
        _ => l10n.aiUsageFeatOther,
      };
}

/// A rounded progress bar on a faint glass track.
class _Bar extends StatelessWidget {
  const _Bar({required this.value, required this.height, required this.color});
  final double value;
  final double height;
  final Color color;

  @override
  Widget build(BuildContext context) => ClipRRect(
        borderRadius: BorderRadius.circular(height),
        child: LinearProgressIndicator(
          value: value,
          minHeight: height,
          color: color,
          backgroundColor: Colors.white.withValues(alpha: 0.08),
        ),
      );
}

class _FeatureRow extends StatelessWidget {
  const _FeatureRow({required this.usage, required this.total, required this.label});
  final AiFeatureUsage usage;
  final int total;
  final String label;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final share = total <= 0 ? 0.0 : (usage.costMicros / total).clamp(0.0, 1.0);
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(child: Text(label, style: cineRowTitleStyle())),
              Text(formatMicros(usage.costMicros), style: GoogleFonts.firaCode(fontSize: 13, color: cineText)),
            ],
          ),
          const SizedBox(height: 6),
          _Bar(value: share, height: 6, color: cineAccent.withValues(alpha: 0.8)),
          const SizedBox(height: 4),
          Text(l10n.aiUsageCalls(usage.calls), style: cineMutedStyle(size: 11)),
        ],
      ),
    );
  }
}
