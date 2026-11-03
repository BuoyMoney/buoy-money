import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import '../../utils/formatters.dart';

// Cinematic palette (shared with the budgets + dashboard pages).
const _income = Color(0xFF34D399); // saved / positive rollover
const _expense = Color(0xFFF87171); // over the set budget
const _pending = Color(0xFFFBBF24); // carried-over overspend
const _accent = Color(0xFF7B61FF); // this month's spend
const _track = Color(0x0DFFFFFF); // empty bar track

/// Signed, color-coded rollover pill. Green when last month's underspend was
/// carried in as bonus room, amber when last month's overspend was carried in.
/// Callers should render this only when rollover is enabled AND the balance is
/// nonzero.
class RolloverChip extends StatelessWidget {
  final double rolloverBalance;

  /// The set budget. When provided, the chip can detect the "spent next month"
  /// case — where the carried overspend has already used up this whole budget
  /// (allocated + rolloverBalance <= 0) — and shows a note plus a mock achievement.
  final double? allocated;

  const RolloverChip({super.key, required this.rolloverBalance, this.allocated});

  bool get _spentNextMonth =>
      rolloverBalance < 0 &&
      allocated != null &&
      (allocated! + rolloverBalance) <= 0;

  @override
  Widget build(BuildContext context) {
    final amount = formatCurrency(rolloverBalance.abs());

    if (_spentNextMonth) {
      // The carried overspend has consumed this month's whole budget, so you've
      // effectively spent next month's money too.
      return Column(
        crossAxisAlignment: CrossAxisAlignment.end,
        mainAxisSize: MainAxisSize.min,
        children: [
          _pill(
            color: _expense,
            icon: Icons.arrow_downward,
            text: '$amount over',
            note: ' · next month',
            tooltip:
                'Overspent $amount — enough to use up next month\'s budget too.',
          ),
          const SizedBox(height: 4),
          _pill(
            color: _accent,
            icon: Icons.emoji_events,
            text: 'Time Traveler',
            tooltip: 'Achievement unlocked: you spent next month\'s money.',
          ),
        ],
      );
    }

    final positive = rolloverBalance > 0;
    return _pill(
      color: positive ? _income : _pending,
      icon: positive ? Icons.arrow_upward : Icons.arrow_downward,
      text: positive ? '$amount saved' : '$amount over',
      tooltip: positive
          ? 'You saved $amount last month, added on top of this budget.'
          : 'You overspent $amount last month, carried into this budget.',
    );
  }

  Widget _pill({
    required Color color,
    required IconData icon,
    required String text,
    required String tooltip,
    String? note,
  }) {
    return Tooltip(
      message: tooltip,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 2),
        decoration: BoxDecoration(
          color: color.withValues(alpha: 0.12),
          borderRadius: BorderRadius.circular(999),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 10, color: color),
            const SizedBox(width: 3),
            Text(text,
                style: GoogleFonts.sora(
                    fontSize: 10, fontWeight: FontWeight.w600, color: color)),
            if (note != null)
              Text(note,
                  style: GoogleFonts.sora(
                      fontSize: 10, color: color.withValues(alpha: 0.7))),
          ],
        ),
      ),
    );
  }
}

/// Honest spend bar for a rollover category: an amber lead-in for overspend
/// carried from last month, the accent fill for this month's spend (red if it
/// breaks the set budget), and a green tail for the bonus room earned by saving
/// last month. The set budget ([allocated]) is the reference; a positive
/// rollover widens the track by the bonus.
class RolloverSpendBar extends StatelessWidget {
  final double allocated;
  final double spent;
  final double rolloverBalance;
  final double height;
  const RolloverSpendBar({
    super.key,
    required this.allocated,
    required this.spent,
    required this.rolloverBalance,
    this.height = 6,
  });

  @override
  Widget build(BuildContext context) {
    final s = spent < 0 ? 0.0 : spent;
    final debt = rolloverBalance < 0 ? -rolloverBalance : 0.0;
    final bonus = rolloverBalance > 0 ? rolloverBalance : 0.0;
    final total = allocated + bonus;
    if (total <= 0) return SizedBox(height: height);

    final spentTracked = s > (total - debt) ? (total - debt) : s;
    final usedInBase =
        (debt + spentTracked) > allocated ? allocated : (debt + spentTracked);
    final baseRemaining = allocated - usedInBase;
    final over = (debt + s) > allocated + 0.5;

    int flex(double v) {
      final f = (v / total * 1000).round();
      return f < 1 ? 1 : f;
    }

    final kids = <Widget>[];
    if (debt > 0) {
      kids.add(Expanded(
          flex: flex(debt),
          child: Container(color: _pending.withValues(alpha: 0.7))));
    }
    if (spentTracked > 0) {
      kids.add(Expanded(
          flex: flex(spentTracked),
          child: Container(
              color: (over ? _expense : _accent).withValues(alpha: 0.75))));
    }
    if (baseRemaining > 0.001) {
      kids.add(Expanded(flex: flex(baseRemaining), child: const SizedBox()));
    }
    if (bonus > 0) {
      kids.add(Expanded(
          flex: flex(bonus),
          child: Container(color: _income.withValues(alpha: 0.45))));
    }
    if (kids.isEmpty) kids.add(const Expanded(child: SizedBox()));

    return SizedBox(
      width: double.infinity,
      height: height,
      child: ClipRRect(
        borderRadius: BorderRadius.circular(3),
        child: Container(color: _track, child: Row(children: kids)),
      ),
    );
  }
}
