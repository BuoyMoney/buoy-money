import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:provider/provider.dart';
import '../l10n/generated/app_localizations.dart';
import '../models/categorization_rule.dart';
import '../providers/auth_provider.dart';
import '../services/categorization_rules_service.dart';
import '../widgets/app_toast.dart';
import '../widgets/cinematic/glass_settings.dart';
import '../widgets/glass_card.dart';

/// Categorization Rules: see, pause and delete the rules that file a merchant automatically.
///
/// Rules are created from the "Always file X as Y?" prompt after a correction; before this page
/// there was no way to undo one. Family-shared rules made by someone else are listed but read-only
/// (the server only lets a rule's creator toggle or delete it).
class CategorizationRulesPage extends StatefulWidget {
  const CategorizationRulesPage({super.key, this.service});

  /// Injected in tests; the app uses the shared instance.
  final CategorizationRulesService? service;

  @override
  State<CategorizationRulesPage> createState() => _CategorizationRulesPageState();
}

class _CategorizationRulesPageState extends State<CategorizationRulesPage> {
  List<CategorizationRule> _rules = [];
  bool _isLoading = true;
  bool _failed = false;

  CategorizationRulesService get _service => widget.service ?? categorizationRulesService;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() {
      _isLoading = true;
      _failed = false;
    });
    try {
      final rules = await _service.listRules();
      if (!mounted) return;
      setState(() {
        _rules = rules;
        _isLoading = false;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _failed = true;
        _isLoading = false;
      });
    }
  }

  bool _canEdit(CategorizationRule rule) {
    // The auth model carries the id as a string.
    final me = int.tryParse(context.read<AuthProvider?>()?.user?.id ?? '');
    // Unknown owner (older server) or unknown user: let the server decide.
    return rule.ownerUserId == 0 || me == null || rule.ownerUserId == me;
  }

  Future<void> _toggle(CategorizationRule rule) async {
    final l10n = AppLocalizations.of(context);
    try {
      final saved = await _service.toggleRule(rule.id);
      if (!mounted) return;
      setState(() {
        _rules = [for (final r in _rules) r.id == rule.id ? rule.copyWith(isActive: saved.isActive) : r];
      });
    } catch (_) {
      if (mounted) showAppToast(context, l10n.rulesActionFailed, type: AppToastType.error);
    }
  }

  Future<void> _delete(CategorizationRule rule) async {
    final l10n = AppLocalizations.of(context);
    final confirm = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        backgroundColor: cineSheetBg,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(16),
          side: const BorderSide(color: cineCardBorder),
        ),
        title: Text(l10n.rulesDeleteTitle,
            style: GoogleFonts.instrumentSerif(fontSize: 24, color: cineText)),
        content: Text(l10n.rulesDeleteBody(rule.matchText), style: cineBodyStyle()),
        actions: [
          GlassTextAction(onPressed: () => Navigator.pop(context, false), label: l10n.rulesCancel, color: cineMuted),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            style: FilledButton.styleFrom(
              backgroundColor: cineExpense.withValues(alpha: 0.9),
              foregroundColor: Colors.white,
              textStyle: GoogleFonts.sora(fontSize: 13, fontWeight: FontWeight.w600),
              shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
            ),
            child: Text(l10n.rulesDelete),
          ),
        ],
      ),
    );
    if (confirm != true) return;
    try {
      await _service.deleteRule(rule.id);
      if (!mounted) return;
      setState(() => _rules = _rules.where((r) => r.id != rule.id).toList());
      showAppToast(context, l10n.rulesDeleted, type: AppToastType.success);
    } catch (_) {
      if (mounted) showAppToast(context, l10n.rulesActionFailed, type: AppToastType.error);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    final List<Widget> children;
    if (_isLoading) {
      children = const [GlassSpinner()];
    } else if (_failed) {
      children = [GlassLoadError(message: l10n.rulesLoadError, retryLabel: l10n.rulesRetry, onRetry: _load)];
    } else {
      children = [
        GlassNote(text: _rules.isEmpty ? l10n.rulesEmpty : l10n.rulesInfo),
        const SizedBox(height: 16),
        ..._rules.map((r) => _RuleCard(
              rule: r,
              canEdit: _canEdit(r),
              onToggle: () => _toggle(r),
              onDelete: () => _delete(r),
            )),
      ];
    }

    return GlassSubPage(
      title: l10n.rulesTitle,
      onRefresh: (_isLoading || _failed) ? null : _load,
      children: children,
    );
  }
}

class _RuleCard extends StatelessWidget {
  const _RuleCard({required this.rule, required this.canEdit, required this.onToggle, required this.onDelete});

  final CategorizationRule rule;
  final bool canEdit;
  final VoidCallback onToggle;
  final VoidCallback onDelete;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    final details = <String>[
      l10n.rulesTimesApplied(rule.timesApplied),
      if (rule.sharedWithFamily) canEdit ? l10n.rulesShared : l10n.rulesSharedByOther,
      if (!rule.isActive) l10n.rulesPaused,
    ];

    return Opacity(
      opacity: rule.isActive ? 1 : 0.6,
      child: GlassCard(
        margin: const EdgeInsets.only(bottom: 12),
        padding: const EdgeInsets.fromLTRB(16, 14, 8, 14),
        borderRadius: 14,
        child: Material(
          type: MaterialType.transparency,
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Padding(
                padding: const EdgeInsets.only(top: 2),
                child: Icon(Icons.rule_rounded, size: 20, color: rule.isActive ? cineAccent : cineMuted),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(rule.matchText, style: cineRowTitleStyle()),
                    if (rule.assignCategoryName != null) ...[
                      const SizedBox(height: 2),
                      Text(l10n.rulesFilesAs(rule.assignCategoryName!), style: cineBodyStyle()),
                    ],
                    const SizedBox(height: 4),
                    Text(details.join(' · '), style: cineMutedStyle()),
                  ],
                ),
              ),
              if (canEdit) ...[
                GlassTextAction(onPressed: onToggle, label: rule.isActive ? l10n.rulesPause : l10n.rulesResume),
                IconButton(
                  tooltip: l10n.rulesDelete,
                  onPressed: onDelete,
                  icon: Icon(Icons.delete_outline, color: cineExpense.withValues(alpha: 0.85)),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}
