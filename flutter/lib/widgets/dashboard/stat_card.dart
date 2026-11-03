import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';

import '../../l10n/generated/app_localizations.dart';
import '../../utils/formatters.dart';

const _cardBg = Color(0x0DFFFFFF);
const _cardBorder = Color(0x14FFFFFF);
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _income = Color(0xFF34D399);
const _expense = Color(0xFFF87171);

/// The dashboard's hero position metric: Net Worth (total cash minus total debt).
///
/// This is the single "how am I doing?" number, so it gets a full-width card above the Cash /
/// Debt pair (its two components) rather than being one tile among equals. It intentionally shows
/// NO month-over-month change or income/spending — those are *flows* and live in the Cash Flow
/// card. Keeping balances (here) and flows (there) apart is the whole point of the redesign.
class DashboardNetWorthCard extends StatelessWidget {
  final double netWorth;
  final VoidCallback? onTap;

  /// ISO codes of foreign-currency accounts excluded from this number
  /// (different currencies can't be summed). Empty = all accounts included.
  final List<String> excludedCurrencyCodes;

  const DashboardNetWorthCard({
    super.key,
    required this.netWorth,
    this.onTap,
    this.excludedCurrencyCodes = const [],
  });

  @override
  Widget build(BuildContext context) {
    // Positive net worth stays neutral (the normal, calm state); only a negative one is flagged
    // red — it's the case worth drawing the eye to.
    final valueColor = netWorth < 0 ? _expense : _textGhost;
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(16),
      child: Container(
        padding: const EdgeInsets.all(20),
        decoration: BoxDecoration(
          color: _cardBg,
          borderRadius: BorderRadius.circular(16),
          border: Border.all(color: _cardBorder, width: 1),
          boxShadow: [
            BoxShadow(
              color: _accent.withValues(alpha: 0.05),
              blurRadius: 28,
            ),
          ],
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            Row(
              children: [
                Container(
                  padding: const EdgeInsets.all(6),
                  decoration: BoxDecoration(
                    color: _accent.withValues(alpha: 0.15),
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: const Icon(Icons.account_balance, color: _accent, size: 16),
                ),
                const SizedBox(width: 8),
                Text(AppLocalizations.of(context).dashNetWorth,
                    style: GoogleFonts.sora(
                        fontSize: 13, fontWeight: FontWeight.w600, color: _textMuted)),
              ],
            ),
            const SizedBox(height: 12),
            FittedBox(
              fit: BoxFit.scaleDown,
              alignment: Alignment.centerLeft,
              child: Text(
                '${netWorth < 0 ? '-' : ''}${formatCurrency(netWorth.abs())}',
                maxLines: 1,
                style: GoogleFonts.firaCode(
                  fontSize: 34,
                  fontWeight: FontWeight.bold,
                  color: valueColor,
                ),
              ),
            ),
            const SizedBox(height: 6),
            Text(
                excludedCurrencyCodes.isEmpty
                    ? AppLocalizations.of(context).netWorthSubtitle
                    : AppLocalizations.of(context)
                        .netWorthSubtitleExcluded(excludedCurrencyCodes.join(', ')),
                style: GoogleFonts.sora(fontSize: 11, color: _textMuted)),
          ],
        ),
      ),
    );
  }
}

/// One of the dashboard summary tiles (Total Cash, Total Debt, …).
///
/// The value and the optional change % share the top row with the label, so the tile is only as
/// tall as its content. This is deliberate: the old layout used `spaceBetween` + a `Spacer`, which
/// pinned the label to the top edge and the number to the bottom edge — fine on a phone's narrow
/// 2-up grid, but on a desktop 4-up grid the tiles are wide (so, at a fixed aspect ratio, tall),
/// and the spacer opened ~100px of dead space in the middle of every card. [compact] collapses
/// that: on desktop the grid asks for compact tiles; on mobile it doesn't, preserving the phone
/// look the design already had.
class DashboardStatCard extends StatelessWidget {
  final String label;
  final String value;
  final IconData icon;
  final Color color;
  final double? change;
  final bool changePositiveIsGood;

  /// Compact = content-height, label and value stacked tight (desktop). Non-compact keeps the
  /// original top-label / bottom-value spread (mobile).
  final bool compact;

  const DashboardStatCard({
    super.key,
    required this.label,
    required this.value,
    required this.icon,
    required this.color,
    this.change,
    this.changePositiveIsGood = true,
    this.compact = false,
  });

  Color get _changeColor =>
      (change! >= 0) == changePositiveIsGood ? _income : _expense;

  Widget _iconBadge() => Container(
        padding: const EdgeInsets.all(6),
        decoration: BoxDecoration(
          color: color.withValues(alpha: 0.15),
          borderRadius: BorderRadius.circular(8),
        ),
        child: Icon(icon, color: color, size: 16),
      );

  Widget _labelText() => Text(
        label,
        style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
      );

  Widget _valueText() => FittedBox(
        fit: BoxFit.scaleDown,
        alignment: Alignment.centerLeft,
        child: Text(
          value,
          maxLines: 1,
          style: GoogleFonts.firaCode(
            fontSize: compact ? 22 : 20,
            fontWeight: FontWeight.bold,
            color: _textGhost,
          ),
        ),
      );

  Widget _changeChip() => Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(change! >= 0 ? Icons.arrow_upward : Icons.arrow_downward,
              size: 12, color: _changeColor),
          const SizedBox(width: 2),
          Text('${change!.abs().toStringAsFixed(1)}%',
              style: GoogleFonts.sora(fontSize: 11, color: _changeColor)),
        ],
      );

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: EdgeInsets.all(compact ? 14 : 16),
      decoration: BoxDecoration(
        color: _cardBg,
        borderRadius: BorderRadius.circular(16),
        border: Border.all(color: _cardBorder, width: 1),
        boxShadow: [
          BoxShadow(
            color: const Color(0xFF7B61FF).withValues(alpha: 0.03),
            blurRadius: 24,
          ),
        ],
      ),
      child: compact ? _buildCompact() : _buildSpread(),
    );
  }

  /// Desktop: label + icon on top, value directly beneath, change % inline beside it. No spacer,
  /// so the tile hugs its content.
  Widget _buildCompact() {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        Row(
          children: [
            Expanded(child: _labelText()),
            _iconBadge(),
          ],
        ),
        const SizedBox(height: 10),
        Row(
          crossAxisAlignment: CrossAxisAlignment.baseline,
          textBaseline: TextBaseline.alphabetic,
          children: [
            Flexible(child: _valueText()),
            if (change != null) ...[
              const SizedBox(width: 8),
              _changeChip(),
            ],
          ],
        ),
      ],
    );
  }

  /// Mobile: the original spread — label pinned top, value + change pinned bottom.
  Widget _buildSpread() {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisAlignment: MainAxisAlignment.spaceBetween,
      children: [
        Row(
          mainAxisAlignment: MainAxisAlignment.spaceBetween,
          children: [
            Expanded(child: _labelText()),
            _iconBadge(),
          ],
        ),
        const Spacer(),
        _valueText(),
        if (change != null) ...[
          const SizedBox(height: 4),
          _changeChip(),
        ],
      ],
    );
  }
}
