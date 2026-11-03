import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';

import '../../l10n/generated/app_localizations.dart';
import '../../models/cash_flow.dart';
import '../../models/savings_models.dart';
import '../../models/subscription_models.dart';
import '../../utils/formatters.dart';

const _accent = Color(0xFF7B61FF);
const _income = Color(0xFF34D399);
const _expense = Color(0xFFF87171);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _cardBg = Color(0x0DFFFFFF);
const _cardBorder = Color(0x14FFFFFF);

/// Shared chrome for the dashboard's summary-row cards: glass surface, an icon + title header,
/// and loading/empty handling — so the three cards read as one set.
class _SummaryCard extends StatelessWidget {
  final IconData icon;
  final String title;
  final Widget child;
  final VoidCallback? onTap;

  const _SummaryCard({
    required this.icon,
    required this.title,
    required this.child,
    this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(16),
      child: Container(
        padding: const EdgeInsets.all(16),
        decoration: BoxDecoration(
          color: _cardBg,
          borderRadius: BorderRadius.circular(16),
          border: Border.all(color: _cardBorder, width: 1),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            Row(
              children: [
                Icon(icon, size: 16, color: _accent),
                const SizedBox(width: 8),
                Text(title,
                    style: GoogleFonts.sora(
                        fontSize: 13, fontWeight: FontWeight.w600, color: _textGhost)),
              ],
            ),
            const SizedBox(height: 14),
            child,
          ],
        ),
      ),
    );
  }
}

Widget _spinner() => const SizedBox(
      height: 60,
      child: Center(
        child: SizedBox(
          width: 18,
          height: 18,
          child: CircularProgressIndicator(strokeWidth: 2, color: _accent),
        ),
      ),
    );

Widget _emptyLine(String text) => Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Text(text, style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
    );

// ── Cash Flow ──────────────────────────────────────────────

/// Actual money in / out / net this month — the honest cash-flow picture, distinct from the
/// budget's planned figures.
class CashFlowCard extends StatelessWidget {
  final CashFlow? cashFlow;
  final bool loading;
  const CashFlowCard({super.key, required this.cashFlow, this.loading = false});

  @override
  Widget build(BuildContext context) {
    final cf = cashFlow;
    return _SummaryCard(
      icon: Icons.swap_vert,
      title: AppLocalizations.of(context).dashCashFlowThisMonth,
      child: loading
          ? _spinner()
          : cf == null
              ? _emptyLine(AppLocalizations.of(context).dashNoActivityThisMonth)
              : Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    _flowRow(AppLocalizations.of(context).dashFlowIn,
                        cf.incomeReceived, _income, Icons.arrow_downward),
                    const SizedBox(height: 8),
                    _flowRow(AppLocalizations.of(context).dashFlowOut,
                        cf.moneySpent, _expense, Icons.arrow_upward),
                    const SizedBox(height: 10),
                    Container(height: 1, color: _cardBorder),
                    const SizedBox(height: 10),
                    Row(
                      mainAxisAlignment: MainAxisAlignment.spaceBetween,
                      children: [
                        Text(AppLocalizations.of(context).dashNet,
                            style: GoogleFonts.sora(
                                fontSize: 13, fontWeight: FontWeight.w600, color: _textGhost)),
                        Text(
                          '${cf.netCashFlow >= 0 ? '+' : '-'}${formatCurrency(cf.netCashFlow.abs())}',
                          style: GoogleFonts.firaCode(
                              fontSize: 15,
                              fontWeight: FontWeight.bold,
                              color: cf.netCashFlow >= 0 ? _income : _expense),
                        ),
                      ],
                    ),
                    if (cf.internalMovementExcluded > 0) ...[
                      const SizedBox(height: 8),
                      Text(
                        AppLocalizations.of(context).dashTransfersExcluded(
                            formatCurrency(cf.internalMovementExcluded)),
                        style: GoogleFonts.sora(fontSize: 10, color: _textMuted),
                      ),
                    ],
                  ],
                ),
    );
  }

  Widget _flowRow(String label, double amount, Color color, IconData icon) => Row(
        mainAxisAlignment: MainAxisAlignment.spaceBetween,
        children: [
          Row(children: [
            Icon(icon, size: 13, color: color),
            const SizedBox(width: 6),
            Text(label, style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
          ]),
          Text(formatCurrency(amount),
              style: GoogleFonts.firaCode(
                  fontSize: 13, fontWeight: FontWeight.w600, color: _textGhost)),
        ],
      );
}

// ── Upcoming Bills ─────────────────────────────────────────

/// The next few subscription renewals + total monthly recurring cost — "what's about to hit
/// your account".
class UpcomingBillsCard extends StatelessWidget {
  final List<Subscription> subscriptions;
  final double monthlyTotal;
  final bool loading;
  final VoidCallback? onViewAll;

  const UpcomingBillsCard({
    super.key,
    required this.subscriptions,
    required this.monthlyTotal,
    this.loading = false,
    this.onViewAll,
  });

  @override
  Widget build(BuildContext context) {
    // Soonest-due active subscriptions, next 3.
    final upcoming = subscriptions
        .where((s) => s.status == SubscriptionStatus.active && s.daysUntilDue >= 0)
        .toList()
      ..sort((a, b) => a.daysUntilDue.compareTo(b.daysUntilDue));
    final next = upcoming.take(3).toList();

    return _SummaryCard(
      icon: Icons.event_repeat,
      title: AppLocalizations.of(context).dashUpcomingBills,
      onTap: onViewAll,
      child: loading
          ? _spinner()
          : Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  crossAxisAlignment: CrossAxisAlignment.baseline,
                  textBaseline: TextBaseline.alphabetic,
                  children: [
                    Text(formatCurrency(monthlyTotal),
                        style: GoogleFonts.firaCode(
                            fontSize: 18, fontWeight: FontWeight.bold, color: _textGhost)),
                    const SizedBox(width: 4),
                    Text(AppLocalizations.of(context).dashPerMonthAbbrev,
                        style: GoogleFonts.sora(fontSize: 11, color: _textMuted)),
                  ],
                ),
                const SizedBox(height: 12),
                if (next.isEmpty)
                  _emptyLine(AppLocalizations.of(context).dashNoUpcomingBills)
                else
                  ...next.map((s) => Padding(
                        padding: const EdgeInsets.only(bottom: 8),
                        child: Row(
                          children: [
                            Expanded(
                              child: Column(
                                crossAxisAlignment: CrossAxisAlignment.start,
                                children: [
                                  Text(s.name,
                                      maxLines: 1,
                                      overflow: TextOverflow.ellipsis,
                                      style: GoogleFonts.sora(fontSize: 12, color: _textGhost)),
                                  Text(_due(context, s.daysUntilDue),
                                      style: GoogleFonts.sora(fontSize: 10, color: _textMuted)),
                                ],
                              ),
                            ),
                            Text(formatCurrency(s.amount),
                                style: GoogleFonts.firaCode(
                                    fontSize: 12, fontWeight: FontWeight.w600, color: _textGhost)),
                          ],
                        ),
                      )),
              ],
            ),
    );
  }

  String _due(BuildContext context, int days) {
    if (days == 0) return AppLocalizations.of(context).dashDueToday;
    if (days == 1) return AppLocalizations.of(context).dashDueTomorrow;
    return AppLocalizations.of(context).dashDueInDays(days);
  }
}

// ── Savings ────────────────────────────────────────────────

/// Money free to save this period + progress toward goals — replaces the old empty Savings Goals
/// column with a live number.
class SavingsSummaryCard extends StatelessWidget {
  final SavingsOverview? overview;
  final bool loading;
  final VoidCallback? onViewAll;
  final VoidCallback? onCreateGoal;

  const SavingsSummaryCard({
    super.key,
    required this.overview,
    this.loading = false,
    this.onViewAll,
    this.onCreateGoal,
  });

  @override
  Widget build(BuildContext context) {
    final o = overview;
    return _SummaryCard(
      icon: Icons.savings_outlined,
      title: AppLocalizations.of(context).dashSavings,
      onTap: onViewAll,
      child: loading
          ? _spinner()
          : o == null
              ? _emptyLine(AppLocalizations.of(context).dashSavingsUnavailable)
              : Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(AppLocalizations.of(context).dashFreeToSave,
                        style: GoogleFonts.sora(fontSize: 11, color: _textMuted)),
                    const SizedBox(height: 2),
                    Text(
                      formatCurrency(o.currentPeriodLeftover),
                      style: GoogleFonts.firaCode(
                          fontSize: 18,
                          fontWeight: FontWeight.bold,
                          color: o.currentPeriodLeftover >= 0 ? _income : _expense),
                    ),
                    const SizedBox(height: 12),
                    if (o.goals.isEmpty)
                      Row(
                        mainAxisAlignment: MainAxisAlignment.spaceBetween,
                        children: [
                          Text(AppLocalizations.of(context).dashNoGoalsYet,
                              style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
                          if (onCreateGoal != null)
                            GestureDetector(
                              onTap: onCreateGoal,
                              child: Text(AppLocalizations.of(context).dashCreate,
                                  style: GoogleFonts.sora(
                                      fontSize: 12,
                                      fontWeight: FontWeight.w600,
                                      color: _accent)),
                            ),
                        ],
                      )
                    else ...[
                      _goalRow(o.goals.first),
                      const SizedBox(height: 6),
                      Text(
                        AppLocalizations.of(context).dashTotalSavedActive(
                            formatCurrency(o.totalSaved), o.activeGoalsCount),
                        style: GoogleFonts.sora(fontSize: 10, color: _textMuted),
                      ),
                    ],
                  ],
                ),
    );
  }

  Widget _goalRow(SavingsGoal g) {
    final pct = (g.progressPercentage / 100).clamp(0.0, 1.0);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          mainAxisAlignment: MainAxisAlignment.spaceBetween,
          children: [
            Expanded(
              child: Text(g.name,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: GoogleFonts.sora(fontSize: 12, color: _textGhost)),
            ),
            Text('${g.progressPercentage.toStringAsFixed(0)}%',
                style: GoogleFonts.sora(fontSize: 11, color: _textMuted)),
          ],
        ),
        const SizedBox(height: 5),
        ClipRRect(
          borderRadius: BorderRadius.circular(3),
          child: LinearProgressIndicator(
            value: pct,
            minHeight: 5,
            backgroundColor: _cardBorder,
            valueColor: const AlwaysStoppedAnimation(_accent),
          ),
        ),
      ],
    );
  }
}
