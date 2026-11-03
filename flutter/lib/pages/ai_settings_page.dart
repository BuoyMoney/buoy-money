import 'package:flutter/material.dart';

import '../l10n/generated/app_localizations.dart';
import '../services/ai_status_service.dart';
import '../widgets/cinematic/glass_settings.dart';

/// Which AI this server uses and what each AI feature can do with it.
///
/// The provider is a server setting (`Ai__Provider` and friends), so this page shows it rather than
/// changing it. The list is the server's own account of what the code does with the provider, not
/// marketing copy: with a local or non-Gemini provider, chat and attachments say "off".
/// An administrator also gets the connection details and a live check.
class AiSettingsPage extends StatefulWidget {
  const AiSettingsPage({super.key, this.service});

  /// Test seam.
  final AiStatusService? service;

  @override
  State<AiSettingsPage> createState() => _AiSettingsPageState();
}

class _AiSettingsPageState extends State<AiSettingsPage> {
  late final AiStatusService _service = widget.service ?? aiStatusService;
  AiStatus? _status;
  String? _error;
  bool _loading = true;

  bool _probing = false;
  List<AiProbeStep>? _probe;
  String? _probeError;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final status = await _service.fetch();
      if (!mounted) return;
      setState(() {
        _status = status;
        _loading = false;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _error = AppLocalizations.of(context).aiSetLoadFailed;
        _loading = false;
      });
    }
  }

  Future<void> _runProbe() async {
    setState(() {
      _probing = true;
      _probe = null;
      _probeError = null;
    });
    try {
      final steps = await _service.probe();
      if (!mounted) return;
      setState(() {
        _probe = steps;
        _probing = false;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _probeError = AppLocalizations.of(context).aiSetCheckFailed;
        _probing = false;
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return GlassSubPage(
      title: l10n.aiSetTitle,
      children: _loading
          ? const [GlassSpinner()]
          : _error != null
              ? [GlassLoadError(message: _error!, retryLabel: l10n.aiSetRetry, onRetry: _load)]
              : _buildBody(context, _status!),
    );
  }

  List<Widget> _buildBody(BuildContext context, AiStatus status) {
    final l10n = AppLocalizations.of(context);
    final details = status.details;
    return [
      _SummaryCard(status: status),
      GlassSection(
        icon: Icons.checklist_rounded,
        title: l10n.aiSetWhatWorks,
        children: [
          Text(l10n.aiSetWhatWorksSub, style: cineMutedStyle()),
          const SizedBox(height: 4),
          for (final (i, feature) in status.features.indexed) ...[
            if (i > 0) const GlassDivider(height: 1),
            _FeatureRow(feature: feature),
          ],
        ],
      ),
      if (details != null)
        GlassSection(
          icon: Icons.dns_outlined,
          title: l10n.aiSetConnection,
          children: [
            _DetailsRows(status: status, details: details),
            const SizedBox(height: 16),
            GlassPrimaryButton(
              key: const ValueKey('ai-set-check'),
              onPressed: (!status.enabled || _probing) ? null : _runProbe,
              busy: _probing,
              icon: Icons.network_check_rounded,
              label: _probing ? l10n.aiSetChecking : l10n.aiSetCheck,
            ),
            if (_probeError != null) ...[
              const SizedBox(height: 12),
              GlassNote(text: _probeError!, tone: GlassNoteTone.error),
            ],
            if (_probe != null) ...[
              const SizedBox(height: 8),
              for (final step in _probe!) _ProbeRow(step: step),
            ],
          ],
        ),
    ];
  }
}

class _SummaryCard extends StatelessWidget {
  const _SummaryCard({required this.status});
  final AiStatus status;

  static const _names = {
    'gemini': 'Google Gemini',
    'ollama': 'Ollama (runs on your own computer)',
    'openai': 'OpenAI',
    'openrouter': 'OpenRouter',
    'gemini-compat': 'Google Gemini (compatibility endpoint)',
    'custom': 'Custom OpenAI-compatible server',
    'buoy-cloud': 'BuoyAI Cloud',
    'none': 'None',
  };

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final name = _names[status.provider] ?? status.provider;
    final setupProblem = status.details?.setupProblem;
    return GlassSection(
      icon: status.enabled ? Icons.auto_awesome_rounded : Icons.block_rounded,
      title: status.enabled ? l10n.aiSetOn : l10n.aiSetOff,
      highlight: status.enabled,
      children: [
        Text(l10n.aiSetProviderLine(name), style: cineBodyStyle()),
        // With AI off, how to turn it on sits right under the status.
        if (!status.enabled) ...[
          if (setupProblem != null) ...[
            const SizedBox(height: 12),
            GlassNote(text: setupProblem, tone: GlassNoteTone.warning),
          ],
          const SizedBox(height: 12),
          GlassNote(text: status.edition == 'selfhost' ? l10n.aiSetOffSelfHost : l10n.aiSetOffHosted),
          const SizedBox(height: 12),
          GlassCodeBlock(text: l10n.aiSetHowTo),
        ],
      ],
    );
  }
}

class _FeatureRow extends StatelessWidget {
  const _FeatureRow({required this.feature});
  final AiFeatureStatus feature;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final (IconData icon, Color color, String label) = switch (feature.status) {
      'works' => (Icons.check_circle_rounded, cineIncome, l10n.aiSetWorks),
      'limited' => (Icons.error_rounded, cineWarning, l10n.aiSetLimited),
      _ => (Icons.remove_circle_outline_rounded, cineMuted, l10n.aiSetNo),
    };
    return Semantics(
      label: '${feature.name}: $label. ${feature.note}',
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 12),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Padding(
              padding: const EdgeInsets.only(top: 1),
              child: Icon(icon, color: color, size: 20),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(feature.name, style: cineRowTitleStyle()),
                  if (feature.note.isNotEmpty)
                    Padding(
                      padding: const EdgeInsets.only(top: 3),
                      child: Text(feature.note, style: cineMutedStyle()),
                    ),
                ],
              ),
            ),
            const SizedBox(width: 10),
            GlassStatusPill(label: label, color: color),
          ],
        ),
      ),
    );
  }
}

class _DetailsRows extends StatelessWidget {
  const _DetailsRows({required this.status, required this.details});
  final AiStatus status;
  final AiProviderDetails details;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final rows = <(String, String, bool)>[
      (l10n.aiSetDetailProvider, status.provider, true),
      if (details.baseUrl != null) (l10n.aiSetDetailAddress, details.baseUrl!, true),
      if (details.model != null && details.model!.isNotEmpty) (l10n.aiSetDetailModel, details.model!, true),
      if (details.embeddingModel.isNotEmpty) (l10n.aiSetDetailEmbedding, details.embeddingModel, true),
      (l10n.aiSetDetailKey, details.apiKeySet ? l10n.aiSetKeySet : l10n.aiSetKeyNotSet, false),
    ];
    return Column(
      children: [
        for (final (label, value, mono) in rows) GlassKeyValueRow(label: label, value: value, mono: mono),
      ],
    );
  }
}

class _ProbeRow extends StatelessWidget {
  const _ProbeRow({required this.step});
  final AiProbeStep step;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final detail = step.milliseconds > 0 ? l10n.aiSetStepWithTime(step.detail, step.milliseconds) : step.detail;
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(step.ok ? Icons.check_circle_rounded : Icons.error_outline_rounded,
              size: 20, color: step.ok ? cineIncome : cineExpense),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(step.name, style: cineRowTitleStyle()),
                const SizedBox(height: 2),
                Text(detail, style: cineMutedStyle()),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
