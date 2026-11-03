import '../config/app_brand.dart';
import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/scheduler.dart';
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:intl/intl.dart';

import '../l10n/generated/app_localizations.dart';
import '../services/analytics_service.dart';
import '../services/review_prompt_service.dart';
import '../services/visualization_service.dart';
import '../services/wrapped_service.dart';
import '../utils/celebration_fx.dart';
import '../utils/wrapped_engagement.dart';
import '../utils/formatters.dart';
import '../widgets/ai/visualization_chart.dart';
import '../widgets/cinematic/share_card.dart';
import '../widgets/spin_up_number.dart';

// ── VaporClinic palette ─────────────────────────────────
const _bg = Color(0xFF04040A);
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _income = Color(0xFF34D399);
const _expense = Color(0xFFF87171);
const _warning = Color(0xFFFBBF24);
const _teal = Color(0xFF4ECDC4);
const _orange = Color(0xFFFF9F45);

/// "Money Wrapped" — the full-screen story recap. Monthly (short, opened from
/// the money_wrapped notification) when [month] is set; the yearly production
/// otherwise. Deliberately OUTSIDE the nav shell (like /connect) so it owns
/// the whole screen: swipe or tap through the cards, share at the end.
class WrappedPage extends StatefulWidget {
  final int year;
  final int? month;

  const WrappedPage({super.key, required this.year, this.month});

  bool get isMonthly => month != null;

  @override
  State<WrappedPage> createState() => _WrappedPageState();
}

class _WrappedPageState extends State<WrappedPage> {
  final _pageCtrl = PageController();
  int _index = 0;
  bool _loading = true;
  bool _error = false;
  MonthlyWrapped? _monthly;
  YearlyWrapped? _yearly;
  bool _celebrated = false;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _recordDwell(_index);
    _pageCtrl.dispose();
    super.dispose();
  }

  /// Log how long the user sat on a card: locally (feeds the year-end
  /// ordering) and to opt-in analytics (feeds the aggregate rotation).
  void _recordDwell(int index) {
    if (index < 0 || index >= _cardTypes.length) return;
    final ms = DateTime.now().difference(_cardShownAt).inMilliseconds;
    _cardShownAt = DateTime.now();
    final type = _cardTypes[index];
    WrappedEngagement.record(type, ms);
    analytics.track('wrapped_card_viewed', {
      'card_type': type,
      'index': index,
      'period': widget.isMonthly ? 'monthly' : 'yearly',
      'dwell_ms': ms,
    });
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = false;
    });
    try {
      _dwellScores = await WrappedEngagement.scores();
      if (widget.isMonthly) {
        _monthly = await wrappedService.getMonthly(widget.year, widget.month!);
      } else {
        _yearly = await wrappedService.getYearly(widget.year);
      }
      if (!mounted) return;
      setState(() => _loading = false);
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _loading = false;
        _error = true;
      });
    }
  }

  String get _monthName => widget.isMonthly
      ? DateFormat.MMMM().format(DateTime(widget.year, widget.month!))
      : '';

  List<Widget> get _cards =>
      widget.isMonthly ? _monthlyCards() : _yearlyCards();

  /// The theme color of each card, in order — drives the morphing background.
  final List<Color> _cardTints = [];

  /// A stable type id per card (cover/money/wins/bonus_x/...), driving dwell
  /// tracking and the engagement-ordered year-end.
  final List<String> _cardTypes = [];

  /// Dwell score snapshot loaded once; orders the yearly middle section.
  Map<String, int> _dwellScores = const {};
  DateTime _cardShownAt = DateTime.now();

  void _next(int cardCount) {
    if (_index < cardCount - 1) {
      _pageCtrl.nextPage(
          duration: const Duration(milliseconds: 350), curve: Curves.easeOutCubic);
    }
  }

  void _prev() {
    if (_index > 0) {
      _pageCtrl.previousPage(
          duration: const Duration(milliseconds: 350), curve: Curves.easeOutCubic);
    }
  }

  void _close() => context.go('/dashboard');

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: _bg,
      body: SafeArea(
        child: _loading
            ? const Center(child: CircularProgressIndicator(color: _accent))
            : _error || (widget.isMonthly ? _monthly == null : _yearly == null)
                ? _buildEmpty()
                : _buildStory(),
      ),
    );
  }

  Widget _buildEmpty() {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          const Text('🎁', style: TextStyle(fontSize: 44)),
          const SizedBox(height: 14),
          Text(AppLocalizations.of(context).wrapNothingToWrapYet,
              style: GoogleFonts.instrumentSerif(fontSize: 24, color: _textGhost)),
          const SizedBox(height: 8),
          Text(AppLocalizations.of(context).wrapEmptyBody,
              style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
          const SizedBox(height: 20),
          TextButton(
            onPressed: _close,
            child: Text(AppLocalizations.of(context).wrapBackToDashboard,
                style: GoogleFonts.sora(
                    fontSize: 13, fontWeight: FontWeight.w600, color: _accent)),
          ),
        ],
      ),
    );
  }

  Widget _buildStory() {
    _cardTints.clear();
    _cardTypes.clear();
    final cards = _cards;
    final tint =
        _index < _cardTints.length ? _cardTints[_index] : _accent;
    return Stack(
      children: [
        // The color story: a dark base with a slowly undulating glow in the
        // current card's color family, warping quickly on page changes.
        Positioned.fill(child: _MorphingGlowBackground(themeColor: tint)),
        // Tap left third = back, anywhere else = forward (story convention).
        GestureDetector(
          behavior: HitTestBehavior.opaque,
          onTapUp: (details) {
            final w = MediaQuery.of(context).size.width;
            if (details.globalPosition.dx < w / 3) {
              _prev();
            } else {
              _next(cards.length);
            }
          },
          child: PageView(
            controller: _pageCtrl,
            onPageChanged: (i) {
              _recordDwell(_index);
              setState(() => _index = i);
              if (i == cards.length - 1 && !_celebrated) {
                _celebrated = true;
                // Finale gets a buzz, not the chime — the chime is reserved
                // for earning something, and this is a recap.
                CelebrationFx.buzz();
              }
            },
            children: cards,
          ),
        ),
        // Progress dashes
        Positioned(
          top: 14,
          left: 20,
          right: 64,
          child: Row(
            children: [
              for (var i = 0; i < cards.length; i++)
                Expanded(
                  child: Container(
                    height: 3,
                    margin: const EdgeInsets.symmetric(horizontal: 2),
                    decoration: BoxDecoration(
                      color: i <= _index
                          ? _accent
                          : Colors.white.withValues(alpha: 0.15),
                      borderRadius: BorderRadius.circular(2),
                    ),
                  ),
                ),
            ],
          ),
        ),
        // Close
        Positioned(
          top: 6,
          right: 12,
          child: IconButton(
            onPressed: _close,
            icon: const Icon(Icons.close, size: 22, color: _textMuted),
          ),
        ),
      ],
    );
  }

  // ── Card scaffolding ──────────────────────────────────

  Widget _card(
      {required Color tint,
      String type = 'card',
      required List<Widget> children}) {
    _cardTints.add(tint);
    _cardTypes.add(type);
    return Padding(
      padding: const EdgeInsets.fromLTRB(32, 64, 32, 48),
      child: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 480),
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: children,
          ),
        ),
      ),
    );
  }

  Widget _kicker(String text, Color color) => Text(text.toUpperCase(),
      style: GoogleFonts.sora(
          fontSize: 12,
          fontWeight: FontWeight.w700,
          letterSpacing: 2.5,
          color: color));

  Widget _serif(String text, {double size = 40}) => Text(text,
      style: GoogleFonts.instrumentSerif(
          fontSize: size, color: _textGhost, height: 1.1));

  Widget _bigMoney(double v, {Color color = _textGhost, double size = 52}) {
    // _cardTints gains this card's entry only after its children are built, so
    // during construction its length IS the index of the card being built.
    final ci = _cardTints.length;
    return SpinUpNumber(
      value: v,
      active: _index == ci,
      format: formatCurrency,
      style: GoogleFonts.firaCode(
          fontSize: size, fontWeight: FontWeight.w700, color: color),
    );
  }

  Widget _muted(String text) =>
      Text(text, style: GoogleFonts.sora(fontSize: 14, color: _textMuted, height: 1.5));

  Widget _tapHint() => Align(
        alignment: Alignment.center,
        child: Padding(
          padding: const EdgeInsets.only(top: 40),
          child: Text(AppLocalizations.of(context).wrapTapToContinue,
              style: GoogleFonts.sora(
                  fontSize: 11, color: _textMuted.withValues(alpha: 0.6))),
        ),
      );

  // ── Monthly story ─────────────────────────────────────

  List<Widget> _monthlyCards() {
    final m = _monthly!;
    final deltaPct = (m.previousMonthSpent != null && m.previousMonthSpent! > 0)
        ? (m.totalSpent - m.previousMonthSpent!) / m.previousMonthSpent! * 100
        : null;

    return [
      _card(tint: _accent, type: 'cover', children: [
        const Text('🎁', style: TextStyle(fontSize: 40)),
        const SizedBox(height: 18),
        _kicker(AppLocalizations.of(context).wrapKickerMoneyWrapped, _accent),
        const SizedBox(height: 8),
        _serif(AppLocalizations.of(context).wrapYourPeriod(_monthName), size: 44),
        _serif(AppLocalizations.of(context).wrapWrapped, size: 44),
        _tapHint(),
      ]),
      _card(tint: _expense, type: 'money', children: [
        _kicker(AppLocalizations.of(context).wrapKickerTheDamage, _expense),
        const SizedBox(height: 14),
        _muted(AppLocalizations.of(context).wrapYouSpent),
        _bigMoney(m.totalSpent),
        const SizedBox(height: 14),
        if (deltaPct != null)
          Row(children: [
            Icon(deltaPct <= 0 ? Icons.trending_down_rounded : Icons.trending_up_rounded,
                size: 18, color: deltaPct <= 0 ? _income : _expense),
            const SizedBox(width: 6),
            Text(
              deltaPct <= 0
                  ? AppLocalizations.of(context).wrapLessThan(
                      deltaPct.abs().toStringAsFixed(0),
                      DateFormat.MMMM().format(
                          DateTime(widget.year, widget.month!, 1)
                              .subtract(const Duration(days: 1))))
                  : AppLocalizations.of(context).wrapMoreThan(
                      deltaPct.abs().toStringAsFixed(0),
                      DateFormat.MMMM().format(
                          DateTime(widget.year, widget.month!, 1)
                              .subtract(const Duration(days: 1)))),
              style: GoogleFonts.sora(
                  fontSize: 13,
                  fontWeight: FontWeight.w600,
                  color: deltaPct <= 0 ? _income : _expense),
            ),
          ]),
        const SizedBox(height: 6),
        _muted(AppLocalizations.of(context).wrapCameInTransactions(
            formatCurrency(m.totalIncome), m.transactionCount)),
      ]),
      if (m.topCategory != null)
        _card(tint: _orange, type: 'category', children: [
          _kicker(AppLocalizations.of(context).wrapKickerWhereItWent, _orange),
          const SizedBox(height: 14),
          _serif(m.topCategory!.name, size: 40),
          const SizedBox(height: 8),
          _bigMoney(m.topCategory!.amount, size: 34, color: _orange),
          _muted(AppLocalizations.of(context).wrapShareOfSpending(
              m.topCategory!.share.toStringAsFixed(0))),
          if (m.topMerchant != null) ...[
            const SizedBox(height: 26),
            _muted(AppLocalizations.of(context).wrapAndYouVisited),
            _serif(m.topMerchant!.name, size: 28),
            _muted(AppLocalizations.of(context)
                .wrapTimes(m.topMerchant!.visits)),
          ],
        ]),
      _card(tint: _income, type: 'wins', children: [
        _kicker(AppLocalizations.of(context).wrapKickerTheWins, _income),
        const SizedBox(height: 18),
        _statRow(m.zeroSpendDays,
            AppLocalizations.of(context).wrapZeroSpendDays, _income,
            emoji: '🛡️'),
        const SizedBox(height: 18),
        _statRow(m.longestStreak,
            AppLocalizations.of(context).wrapDayStreakGoing, _orange,
            emoji: '🔥'),
        const SizedBox(height: 18),
        _statRow(
            m.badgesEarned.length,
            AppLocalizations.of(context)
                .wrapBadgesEarnedLabel(m.badgesEarned.length),
            _warning,
            emoji: '🏆'),
        if (m.badgesEarned.isNotEmpty) ...[
          const SizedBox(height: 14),
          Row(
            children: m.badgesEarned
                .take(5)
                .map((b) => Padding(
                      padding: const EdgeInsets.only(right: 8),
                      child: Image.asset(
                        'assets/achievements/${b.key}.png',
                        width: 44,
                        height: 44,
                        errorBuilder: (_, __, ___) =>
                            Text(b.icon, style: const TextStyle(fontSize: 30)),
                      ),
                    ))
                .toList(),
          ),
        ],
      ]),
      _card(tint: _teal, type: 'saved', children: [
        _kicker(AppLocalizations.of(context).wrapKickerPaidYourself, _teal),
        const SizedBox(height: 14),
        if (m.savedToGoals > 0) ...[
          _muted(AppLocalizations.of(context).wrapYouMoved),
          _bigMoney(m.savedToGoals, color: _teal),
          _muted(AppLocalizations.of(context).wrapIntoSavingsGoals),
        ] else ...[
          const Text('🌱', style: TextStyle(fontSize: 40)),
          const SizedBox(height: 12),
          _serif(AppLocalizations.of(context).wrapNextMonth, size: 32),
          _serif(AppLocalizations.of(context).wrapPayYourselfFirst, size: 32),
        ],
      ]),
      ..._bonusCards(m.bonusCards),
      _card(tint: _accent, type: 'share', children: [
        _kicker(
            AppLocalizations.of(context).wrapThatWasPeriod(_monthName), _accent),
        const SizedBox(height: 14),
        _serif(AppLocalizations.of(context).wrapSeeYouNext, size: 38),
        _serif(AppLocalizations.of(context).wrapMonthDot, size: 38),
        const SizedBox(height: 30),
        _shareButton(
          title: '$_monthName ${widget.year}',
          subtitle: _shareSubtitle(
              m.zeroSpendDays, m.longestStreak, m.badgesEarned.length),
          caption:
              'My $_monthName, wrapped 🎁 ${m.zeroSpendDays} zero-spend days on ${AppBrand.name}. buoy-ai.com',
        ),
        const SizedBox(height: 12),
        _doneButton(),
      ]),
    ];
  }

  // ── Yearly story ──────────────────────────────────────

  List<Widget> _yearlyCards() {
    final y = _yearly!;
    final cards = <Widget>[
      _card(tint: _accent, type: 'cover', children: [
        const Text('🎉', style: TextStyle(fontSize: 40)),
        const SizedBox(height: 18),
        _kicker('money wrapped', _accent),
        const SizedBox(height: 8),
        _serif('Your ${y.year},', size: 48),
        _serif('wrapped.', size: 48),
        _tapHint(),
      ]),
      _card(tint: _income, type: 'money', children: [
        _kicker('the year in money', _income),
        const SizedBox(height: 16),
        _muted('In'),
        _bigMoney(y.totalIncome, color: _income, size: 40),
        const SizedBox(height: 16),
        _muted('Out'),
        _bigMoney(y.totalSpent, size: 40),
        const SizedBox(height: 10),
        _muted('${y.transactionCount} transactions across 12 months'),
      ]),
      _card(tint: _accent, type: 'shape', children: [
        _kicker('the shape of your year', _accent),
        const SizedBox(height: 20),
        SizedBox(height: 140, child: _monthlyBars(y.monthlySpend)),
        const SizedBox(height: 10),
        if (y.biggestSpendDay != null)
          _muted(
              'Biggest single day: ${formatCurrency(y.biggestSpendDay!.amount)} on ${DateFormat.MMMd().format(y.biggestSpendDay!.date)}'),
      ]),
      if (y.topCategories.isNotEmpty)
        _card(tint: _orange, type: 'category', children: [
          _kicker('where it all went', _orange),
          const SizedBox(height: 14),
          VisualizationChart(
            bare: true,
            height: 210,
            viz: UserVisualization(
              id: -1,
              chartType: 'donut',
              title: '',
              message: '',
              motivationalMessage: null,
              chartData: y.topCategories
                  .map((c) => ChartDataPoint(name: c.name, value: c.amount))
                  .toList(),
              chartConfig: ChartConfig(valuePrefix: r'$'),
              category: 'spending',
              priority: 'low',
              confidence: 1,
              isStarred: false,
              isDismissed: false,
              viewCount: 0,
              lastViewedAt: null,
              generatedAt: DateTime(widget.year, 12, 31),
            ),
          ),
        ]),
      if (y.topMerchant != null)
        _card(tint: _teal, type: 'merchant', children: [
          _kicker('your spot', _teal),
          const SizedBox(height: 14),
          _serif(y.topMerchant!.name, size: 40),
          const SizedBox(height: 8),
          _muted(
              '${y.topMerchant!.visits} visits · ${formatCurrency(y.topMerchant!.amount)}'),
          const SizedBox(height: 6),
          _muted('They should know your name by now.'),
        ]),
      _card(tint: _income, type: 'wins', children: [
        _kicker('the wins', _income),
        const SizedBox(height: 18),
        _statRow(y.zeroSpendDays, 'zero-spend days', _income, emoji: '🛡️'),
        const SizedBox(height: 18),
        _statRow(y.longestStreak, 'day streak at its longest', _orange,
            emoji: '🔥'),
        const SizedBox(height: 18),
        _statRow(y.badgesEarnedCount, 'badges earned', _warning, emoji: '🏆'),
        if (y.bestBadge != null) ...[
          const SizedBox(height: 20),
          Row(children: [
            Image.asset(
              'assets/achievements/${y.bestBadge!.key}.png',
              width: 64,
              height: 64,
              errorBuilder: (_, __, ___) =>
                  Text(y.bestBadge!.icon, style: const TextStyle(fontSize: 40)),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Text(y.bestBadge!.title,
                    style: GoogleFonts.sora(
                        fontSize: 15,
                        fontWeight: FontWeight.w700,
                        color: _textGhost)),
                Text(AppLocalizations.of(context).wrapRarestCatch(y.year),
                    style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
              ]),
            ),
          ]),
        ],
      ]),
      _card(tint: _teal, type: 'saved', children: [
        _kicker('paid yourself', _teal),
        const SizedBox(height: 14),
        if (y.savedToGoals > 0) ...[
          _muted('Moved to savings goals'),
          _bigMoney(y.savedToGoals, color: _teal, size: 44),
          _muted('Future you says thanks.'),
        ] else ...[
          const Text('🌱', style: TextStyle(fontSize: 40)),
          const SizedBox(height: 12),
          _serif('${y.year + 1} goal:', size: 34),
          _serif('pay yourself first.', size: 34),
        ],
      ]),
      ..._bonusCards(y.bonusCards),
      _card(tint: _accent, type: 'share', children: [
        _kicker('that was ${y.year}', _accent),
        const SizedBox(height: 14),
        _serif('Here\'s to', size: 42),
        _serif('${y.year + 1}.', size: 42),
        const SizedBox(height: 30),
        _shareButton(
          title: '${y.year}',
          subtitle:
              _shareSubtitle(y.zeroSpendDays, y.longestStreak, y.badgesEarnedCount),
          caption:
              'My ${y.year}, wrapped 🎉 ${y.zeroSpendDays} zero-spend days and ${y.badgesEarnedCount} badges on ${AppBrand.name}. buoy-ai.com',
        ),
        const SizedBox(height: 12),
        _doneButton(),
      ]),
    ];

    // Year-end plays the hits: the middle cards (everything between the cover
    // and the finale block) are ordered by how long this user lingered on the
    // matching card types across the year's monthly stories. Unseen types
    // keep their default order (stable sort).
    if (_dwellScores.isNotEmpty && cards.length > 3) {
      final bonusCount = y.bonusCards.length;
      final start = 1;
      final end = cards.length - 1 - bonusCount; // keep bonus + finale placement
      if (end - start > 1) {
        final middle = List.generate(
            end - start, (i) => (card: cards[start + i], type: _cardTypes[start + i]));
        final indexed = middle.asMap().entries.toList();
        indexed.sort((a, b) {
          final sa = _dwellScores[a.value.type] ?? 0;
          final sb = _dwellScores[b.value.type] ?? 0;
          final byScore = sb.compareTo(sa);
          return byScore != 0 ? byScore : a.key.compareTo(b.key);
        });
        for (var i = 0; i < indexed.length; i++) {
          cards[start + i] = indexed[i].value.card;
          _cardTypes[start + i] = indexed[i].value.type;
        }
      }
    }
    return cards;
  }

  /// Bonus story cards: AI/rotation picks + the percentile, rendered in a
  /// rotating set of family colors before the finale.
  List<Widget> _bonusCards(List<WrappedBonusCard> cards) {
    const tints = [_warning, _teal, _orange];
    return [
      for (var i = 0; i < cards.length; i++)
        _card(
          tint: cards[i].source == 'percentile' ? _accent : tints[i % tints.length],
          type: cards[i].source == 'percentile'
              ? 'percentile'
              : 'bonus_${cards[i].factKey}',
          children: [
            _kicker(
                cards[i].kicker.isEmpty ? 'did you know' : cards[i].kicker,
                cards[i].source == 'percentile'
                    ? _accent
                    : tints[i % tints.length]),
            const SizedBox(height: 14),
            Text(cards[i].value,
                style: GoogleFonts.firaCode(
                    fontSize: 46,
                    fontWeight: FontWeight.w700,
                    color: _textGhost)),
            const SizedBox(height: 6),
            _serif(cards[i].headline, size: 30),
            if (cards[i].detail.isNotEmpty) ...[
              const SizedBox(height: 10),
              _muted(cards[i].detail),
            ],
          ],
        ),
    ];
  }

  // ── Shared bits ───────────────────────────────────────

  Widget _statRow(num value, String label, Color color,
      {required String emoji}) {
    final ci = _cardTints.length; // see _bigMoney: index of the card being built
    return Row(children: [
      Text(emoji, style: const TextStyle(fontSize: 26)),
      const SizedBox(width: 14),
      SpinUpNumber(
          value: value.toDouble(),
          active: _index == ci,
          format: (v) => v.round().toString(),
          style: GoogleFonts.firaCode(
              fontSize: 36, fontWeight: FontWeight.w700, color: color)),
      const SizedBox(width: 10),
      Expanded(
        child:
            Text(label, style: GoogleFonts.sora(fontSize: 14, color: _textMuted)),
      ),
    ]);
  }

  Widget _monthlyBars(List<double> spend) {
    final maxSpend = spend.isEmpty ? 0.0 : spend.reduce((a, b) => a > b ? a : b);
    const labels = ['J', 'F', 'M', 'A', 'M', 'J', 'J', 'A', 'S', 'O', 'N', 'D'];
    return Row(
      crossAxisAlignment: CrossAxisAlignment.end,
      children: [
        for (var i = 0; i < 12; i++)
          Expanded(
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 3),
              child: Column(
                mainAxisAlignment: MainAxisAlignment.end,
                children: [
                  Container(
                    height: maxSpend > 0
                        ? (100 * (i < spend.length ? spend[i] : 0) / maxSpend)
                            .clamp(2, 100)
                            .toDouble()
                        : 2,
                    decoration: BoxDecoration(
                      gradient: LinearGradient(
                        begin: Alignment.bottomCenter,
                        end: Alignment.topCenter,
                        colors: [
                          _accent.withValues(alpha: 0.45),
                          _accent,
                        ],
                      ),
                      borderRadius:
                          const BorderRadius.vertical(top: Radius.circular(3)),
                    ),
                  ),
                  const SizedBox(height: 6),
                  Text(labels[i],
                      style: GoogleFonts.sora(fontSize: 9, color: _textMuted)),
                ],
              ),
            ),
          ),
      ],
    );
  }

  String _shareSubtitle(int zeroDays, int streak, int badges) {
    final parts = <String>[
      if (zeroDays > 0) '$zeroDays zero-spend days',
      if (streak > 0) '$streak-day streak',
      if (badges > 0) '$badges badge${badges == 1 ? '' : 's'}',
    ];
    return parts.isEmpty ? 'A month of showing up' : parts.join(' · ');
  }

  Widget _shareButton(
      {required String title, required String subtitle, required String caption}) {
    return SizedBox(
      width: double.infinity,
      child: FilledButton.icon(
        style: FilledButton.styleFrom(
          backgroundColor: _accent,
          padding: const EdgeInsets.symmetric(vertical: 14),
        ),
        onPressed: () => showShareCardSheet(
          context,
          // Privacy rule: share cards carry NO dollar amounts.
          ShareCardData(
            kicker: widget.isMonthly ? 'MY MONTH, WRAPPED' : 'MY YEAR, WRAPPED',
            icon: widget.isMonthly ? '🎁' : '🎉',
            title: title,
            subtitle: subtitle,
          ),
          caption: caption,
        ),
        icon: const Icon(Icons.ios_share, size: 18),
        label: Text(AppLocalizations.of(context).wrapShareIt,
            style:
                GoogleFonts.sora(fontWeight: FontWeight.w700, color: Colors.white)),
      ),
    );
  }

  Widget _doneButton() {
    return SizedBox(
      width: double.infinity,
      child: TextButton(
        onPressed: () {
          // Finishing the story is a win; the review ask (if this device has
          // earned one) lands over the dashboard a beat after the exit.
          unawaited(reviewPrompts.maybeRequest(ReviewMoment.wrappedFinished));
          _close();
        },
        child: Text(AppLocalizations.of(context).wrapDone,
            style: GoogleFonts.sora(
                fontSize: 13, fontWeight: FontWeight.w600, color: _textMuted)),
      ),
    );
  }
}


// ═══════════════════════════════════════════════════════════
//  Ocean swell background
// ═══════════════════════════════════════════════════════════

/// The Wrapped color story, done the buoy way: a dark sea at night. Three
/// stacked swells roll across the screen at different speeds and directions
/// (two sine harmonics each, so no crest repeats), their amplitude breathing,
/// under a soft moonlight glow. Everything lives in the current card's color
/// family — the hue wanders a few degrees but always centers on the theme.
///
/// A page turn injects energy: the water surges and the palette warps quickly
/// to the next card's family while the data flies in, then the sea calms back
/// to its slow roll. Cost: three filled wave paths + one radial gradient per
/// frame; static gradient under reduce-motion.
class _MorphingGlowBackground extends StatefulWidget {
  final Color themeColor;
  const _MorphingGlowBackground({required this.themeColor});

  @override
  State<_MorphingGlowBackground> createState() =>
      _MorphingGlowBackgroundState();
}

class _MorphingGlowBackgroundState extends State<_MorphingGlowBackground>
    with SingleTickerProviderStateMixin {
  late final Ticker _ticker;
  Duration _lastTick = Duration.zero;

  double _t = 0; // sea time, in seconds (advances faster under energy)
  double _energy = 1.0; // 1 = calm roll; bursts on theme change, then decays
  late Color _color = widget.themeColor;

  @override
  void initState() {
    super.initState();
    _ticker = createTicker(_onTick)..start();
  }

  @override
  void didUpdateWidget(_MorphingGlowBackground old) {
    super.didUpdateWidget(old);
    if (old.themeColor != widget.themeColor) {
      _energy = 6.0; // page turn: the sea surges toward the new family
    }
  }

  @override
  void dispose() {
    _ticker.dispose();
    super.dispose();
  }

  void _onTick(Duration elapsed) {
    final dt =
        ((elapsed - _lastTick).inMicroseconds / 1e6).clamp(0.0, 0.1);
    _lastTick = elapsed;
    if (dt == 0) return;

    _t += dt * _energy;
    _energy = 1 + (_energy - 1) * math.exp(-dt * 2.0);
    final chase = 1 - math.exp(-dt * (1.0 + 2.8 * (_energy - 1)));
    _color = Color.lerp(_color, widget.themeColor, chase)!;

    if (mounted) setState(() {});
  }

  @override
  Widget build(BuildContext context) {
    final reduceMotion =
        MediaQuery.maybeOf(context)?.disableAnimations ?? false;
    if (reduceMotion) {
      return DecoratedBox(
        decoration: BoxDecoration(
          gradient: RadialGradient(
            center: const Alignment(0, -0.5),
            radius: 1.4,
            colors: [widget.themeColor.withValues(alpha: 0.16), _bg],
          ),
        ),
      );
    }
    return CustomPaint(
      painter: _OceanSwellPainter(t: _t, color: _color, energy: _energy),
      child: const SizedBox.expand(),
    );
  }
}

/// One band of the sea: where it sits, how big the shared sea reads at this
/// depth, and how fast it scrolls past (nearer = faster, classic parallax).
class _SeaBand {
  final double baseY; // surface height, fraction of screen height
  final double amp; // amplitude at this depth, fraction of screen height
  final double stretch; // horizontal scale applied to the shared sea
  final double shift; // horizontal offset into the shared sea
  final double flow; // time multiplier: nearer bands scroll faster
  final double alpha;
  final double hueOffset;

  const _SeaBand({
    required this.baseY,
    required this.amp,
    required this.stretch,
    required this.shift,
    required this.flow,
    required this.alpha,
    required this.hueOffset,
  });
}

class _OceanSwellPainter extends CustomPainter {
  final double t;
  final Color color;
  final double energy;

  const _OceanSwellPainter(
      {required this.t, required this.color, required this.energy});

  // ONE shared sea, sampled by every band: three traveling sinusoid
  // components, all moving the same direction, the long swell fastest
  // (real dispersion). Because the components TRAVEL (phase moves with
  // time at a per-wavelength speed), nothing stands still — and because
  // every band samples the same function, a passing swell reads through
  // all of them like one body of water.
  //   (wavelength in screen-widths, weight, speed in widths/second)
  static const _components = [
    (lam: 1.90, w: 1.00, v: 0.085),
    (lam: 0.85, w: 0.45, v: 0.052),
    (lam: 0.42, w: 0.22, v: 0.034),
  ];

  static double _sea(double u, double time) {
    var y = 0.0;
    for (final c in _components) {
      y += c.w * math.sin(2 * math.pi * (u - c.v * time) / c.lam);
    }
    return y / 1.67; // normalize to roughly [-1, 1]
  }

  // Back to front: stacked close, gentle amplitudes, nearer bands larger,
  // faster-flowing, and brighter. Alternating slight hue offsets keep the
  // family readable where crests cross (overlapping translucent fills
  // naturally deepen at crossings).
  static const _bands = [
    _SeaBand(baseY: 0.70, amp: 0.016, stretch: 1.00, shift: 0.00, flow: 1.0,
        alpha: 0.08, hueOffset: -6),
    _SeaBand(baseY: 0.765, amp: 0.022, stretch: 1.25, shift: 0.37, flow: 1.55,
        alpha: 0.12, hueOffset: 0),
    _SeaBand(baseY: 0.83, amp: 0.028, stretch: 1.55, shift: 0.71, flow: 2.2,
        alpha: 0.16, hueOffset: 6),
  ];

  @override
  void paint(Canvas canvas, Size size) {
    canvas.drawRect(Offset.zero & size, Paint()..color = _bg);

    final hsl = HSLColor.fromColor(color);
    // The whole sea swells a little during the page-turn surge.
    final surge = 1 + 0.25 * (energy - 1) / 5;

    // Moonlight: a soft glow drifting near the top, lighting the water.
    final moonX = size.width * (0.5 + 0.16 * math.sin(t * 0.09 + 2.0));
    final moonHue = (hsl.hue + 5 * math.sin(t * 0.07)) % 360.0;
    final moonColor =
        hsl.withHue(moonHue < 0 ? moonHue + 360 : moonHue).toColor();
    final moonCenter = Offset(moonX, size.height * 0.16);
    final moonRadius = size.longestSide * 0.62;
    canvas.drawCircle(
      moonCenter,
      moonRadius,
      Paint()
        ..shader = RadialGradient(colors: [
          moonColor.withValues(alpha: 0.12),
          moonColor.withValues(alpha: 0.0),
        ]).createShader(
            Rect.fromCircle(center: moonCenter, radius: moonRadius)),
    );

    for (final band in _bands) {
      final amp = size.height *
          band.amp *
          surge *
          (1 + 0.12 * math.sin(t * 0.19 + band.shift * 9));
      final baseY = size.height * band.baseY;
      final hue = (hsl.hue +
              band.hueOffset +
              3 * math.sin(t * 0.08 + band.shift * 7)) %
          360.0;
      final waveColor = hsl.withHue(hue < 0 ? hue + 360 : hue).toColor();

      // Sample the shared sea at this band's depth, then draw a SMOOTH
      // surface with quadratic midpoint curves — no visible segments.
      const steps = 26;
      final pts = List<Offset>.generate(steps + 1, (i) {
        final u = i / steps;
        final y = baseY +
            amp * _sea(u * band.stretch + band.shift, t * band.flow);
        return Offset(size.width * u, y);
      });
      var surfaceMin = double.infinity;
      for (final pt in pts) {
        if (pt.dy < surfaceMin) surfaceMin = pt.dy;
      }

      final path = Path()
        ..moveTo(0, size.height)
        ..lineTo(pts.first.dx, pts.first.dy);
      for (var i = 0; i < pts.length - 1; i++) {
        final mid = Offset((pts[i].dx + pts[i + 1].dx) / 2,
            (pts[i].dy + pts[i + 1].dy) / 2);
        path.quadraticBezierTo(pts[i].dx, pts[i].dy, mid.dx, mid.dy);
      }
      path
        ..lineTo(pts.last.dx, pts.last.dy)
        ..lineTo(size.width, size.height)
        ..close();

      // Soft crest: the gradient FADES IN from the surface line (alpha 0 at
      // the very edge), peaks just below it, then dissolves into the deep —
      // a feathered edge with zero blur cost.
      final shaderRect = Rect.fromLTRB(
          0, surfaceMin, size.width, surfaceMin + size.height * 0.5);
      canvas.drawPath(
        path,
        Paint()
          ..shader = LinearGradient(
            begin: Alignment.topCenter,
            end: Alignment.bottomCenter,
            stops: const [0.0, 0.14, 1.0],
            colors: [
              waveColor.withValues(alpha: 0.0),
              waveColor.withValues(alpha: band.alpha),
              waveColor.withValues(alpha: 0.0),
            ],
          ).createShader(shaderRect),
      );
    }
  }

  @override
  bool shouldRepaint(_OceanSwellPainter old) =>
      old.t != t || old.color != color || old.energy != energy;
}
