import '../l10n/generated/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import '../services/onboarding_quiz_service.dart';

// ── VaporClinic palette ─────────────────────────────────
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _cardBg = Color(0x0DFFFFFF);
const _cardBorder = Color(0x14FFFFFF);
const _income = Color(0xFF34D399);
const _expense = Color(0xFFF87171);

class _QuizQuestion {
  final String key;
  final String title;
  final bool isRadio;
  final List<String> options;
  final int? maxSelections;

  const _QuizQuestion({
    required this.key,
    required this.title,
    required this.isRadio,
    required this.options,
    this.maxSelections,
  });
}

const _questions = <_QuizQuestion>[
  _QuizQuestion(
    key: 'primaryFinancialGoal',
    title: 'What are your primary financial goals?',
    isRadio: false,
    maxSelections: 3,
    options: [
      'Save for emergency fund',
      'Pay off debt',
      'Save for a major purchase',
      'Build wealth for retirement',
      'Improve credit score',
      'Better budgeting and spending control',
      'Increase income',
      'Plan for major life events',
    ],
  ),
  _QuizQuestion(
    key: 'biggestChallenge',
    title: 'What are your biggest financial challenges?',
    isRadio: false,
    maxSelections: 3,
    options: [
      'Not saving enough money',
      'Overspending on non-essentials',
      'Managing debt payments',
      'Understanding where my money goes',
      'Planning for the future',
      'Dealing with irregular income',
      'High cost of living',
      'Lack of financial knowledge',
    ],
  ),
  _QuizQuestion(
    key: 'spendingPersonality',
    title: 'Which spending personalities describe you?',
    isRadio: false,
    maxSelections: 2,
    options: [
      'Careful planner - I budget everything',
      'Impulse buyer - I often make unplanned purchases',
      'Bargain hunter - I always look for deals',
      'Quality focused - I prefer to buy fewer, better things',
      'Convenience focused - I value time over money',
      'Social spender - I spend more when with others',
      'Anxiety spender - I spend when stressed',
      'Goal-oriented saver - I save for specific targets',
    ],
  ),
  _QuizQuestion(
    key: 'subscriptionAwareness',
    title: 'How aware are you of your recurring subscriptions?',
    isRadio: true,
    options: [
      'Very aware - I track all my subscriptions',
      'Somewhat aware - I know most of them',
      'Not very aware - I forget about some',
      'Not aware at all - I have no idea what I pay for',
    ],
  ),
  _QuizQuestion(
    key: 'savingsBarriers',
    title: 'What prevents you from saving more?',
    isRadio: false,
    options: [
      'Not enough income',
      'Too many expenses',
      'Lack of motivation',
      "Don't know how to start",
      'Unexpected expenses keep coming up',
      'I prioritize current enjoyment over future savings',
      'High debt payments',
      'No clear savings goals',
    ],
  ),
  _QuizQuestion(
    key: 'preferredInsightType',
    title: 'What types of financial insights would be most helpful?',
    isRadio: false,
    maxSelections: 3,
    options: [
      'Spending pattern analysis',
      'Savings optimization tips',
      'Budget recommendations',
      'Debt payoff strategies',
      'Investment suggestions',
      'Bill and subscription alerts',
      'Goal tracking and progress',
      'Cashflow forecasting',
    ],
  ),
  _QuizQuestion(
    key: 'financialExperience',
    title: 'How would you rate your financial experience?',
    isRadio: true,
    options: [
      "Beginner - I'm just starting to learn",
      'Intermediate - I know the basics',
      'Advanced - I have good financial knowledge',
      'Expert - I manage complex financial situations',
    ],
  ),
];

// ═══════════════════════════════════════════════════════════
//  AiQuizPage
// ═══════════════════════════════════════════════════════════
class AiQuizPage extends StatefulWidget {
  const AiQuizPage({super.key});

  @override
  State<AiQuizPage> createState() => _AiQuizPageState();
}

class _AiQuizPageState extends State<AiQuizPage>
    with SingleTickerProviderStateMixin {
  int _currentQuestion = 0;
  final Map<String, List<String>> _answers = {};
  bool _submitting = false;
  bool _completed = false;
  String? _error;

  late AnimationController _fadeController;
  late Animation<double> _fadeAnim;

  @override
  void initState() {
    super.initState();
    _fadeController = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 600),
    );
    _fadeAnim = CurvedAnimation(parent: _fadeController, curve: Curves.easeOut);
    _fadeController.forward();
    _checkExistingQuiz();
  }

  @override
  void dispose() {
    _fadeController.dispose();
    super.dispose();
  }

  Future<void> _checkExistingQuiz() async {
    try {
      final existing = await onboardingQuizService.getLatestQuiz();
      if (existing != null && mounted) {
        _prefillAnswers(existing);
      }
    } catch (_) {}
  }

  void _prefillAnswers(OnboardingQuizResponse r) {
    setState(() {
      _answers['primaryFinancialGoal'] = _splitCsv(r.primaryFinancialGoal);
      _answers['biggestChallenge'] = _splitCsv(r.biggestChallenge);
      _answers['spendingPersonality'] = _splitCsv(r.spendingPersonality);
      _answers['subscriptionAwareness'] = [r.subscriptionAwareness];
      if (r.savingsBarriers != null) {
        _answers['savingsBarriers'] = _splitCsv(r.savingsBarriers!);
      }
      _answers['preferredInsightType'] = _splitCsv(r.preferredInsightType);
      _answers['financialExperience'] = [r.financialExperience];
    });
  }

  List<String> _splitCsv(String value) {
    return value.split(', ').where((s) => s.isNotEmpty).toList();
  }

  void _toggleOption(String key, String option, _QuizQuestion q) {
    setState(() {
      final list = _answers[key] ?? [];

      if (q.isRadio) {
        _answers[key] = [option];
        return;
      }

      if (list.contains(option)) {
        list.remove(option);
      } else {
        if (q.maxSelections != null && list.length >= q.maxSelections!) return;
        list.add(option);
      }
      _answers[key] = list;
    });
  }

  bool get _hasCurrentAnswer {
    final q = _questions[_currentQuestion];
    final a = _answers[q.key];
    return a != null && a.isNotEmpty;
  }

  void _next() {
    if (_currentQuestion < _questions.length - 1) {
      setState(() => _currentQuestion++);
    }
  }

  void _prev() {
    if (_currentQuestion > 0) {
      setState(() => _currentQuestion--);
    }
  }

  Future<void> _submit() async {
    setState(() {
      _submitting = true;
      _error = null;
    });

    try {
      final submission = OnboardingQuizSubmission(
        primaryFinancialGoal:
            (_answers['primaryFinancialGoal'] ?? []).join(', '),
        biggestChallenge: (_answers['biggestChallenge'] ?? []).join(', '),
        spendingPersonality:
            (_answers['spendingPersonality'] ?? []).join(', '),
        subscriptionAwareness:
            (_answers['subscriptionAwareness'] ?? []).join(', '),
        savingsBarriers: (_answers['savingsBarriers'] ?? []).isNotEmpty
            ? (_answers['savingsBarriers'] ?? []).join(', ')
            : null,
        preferredInsightType:
            (_answers['preferredInsightType'] ?? []).join(', '),
        financialExperience:
            (_answers['financialExperience'] ?? []).join(', '),
      );

      await onboardingQuizService.submitQuiz(submission);
      if (mounted) {
        setState(() => _completed = true);
      }
    } catch (e) {
      if (mounted) {
        setState(
            () => _error = 'Failed to submit quiz. Please try again.');
      }
    } finally {
      if (mounted) setState(() => _submitting = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.transparent,
      body: FadeTransition(
        opacity: _fadeAnim,
        child: _completed ? _buildSuccess() : _buildQuiz(),
      ),
    );
  }

  // ── Success screen ─────────────────────────────────────

  Widget _buildSuccess() {
    return Center(
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Container(
              width: 72,
              height: 72,
              decoration: BoxDecoration(
                shape: BoxShape.circle,
                color: _income.withValues(alpha: 0.15),
                border: Border.all(
                    color: _income.withValues(alpha: 0.3)),
              ),
              child: const Icon(Icons.check_rounded,
                  size: 36, color: _income),
            ),
            const SizedBox(height: 24),
            Text(
              AppLocalizations.of(context).quizProfileComplete,
              style: GoogleFonts.instrumentSerif(
                fontSize: 28,
                fontWeight: FontWeight.w400,
                color: _textGhost,
              ),
            ),
            const SizedBox(height: 12),
            Text(
              AppLocalizations.of(context).quizProfileCompleteBody,
              textAlign: TextAlign.center,
              style: GoogleFonts.sora(fontSize: 14, color: _textMuted),
            ),
            const SizedBox(height: 32),
            _SolidBtn(
              label: 'Back to Settings',
              onTap: () => Navigator.of(context).pop(),
            ),
          ],
        ),
      ),
    );
  }

  // ── Quiz body ──────────────────────────────────────────

  Widget _buildQuiz() {
    final q = _questions[_currentQuestion];
    final selected = _answers[q.key] ?? [];
    final isLast = _currentQuestion == _questions.length - 1;
    final progress = (_currentQuestion + 1) / _questions.length;

    return ListView(
      padding: const EdgeInsets.fromLTRB(24, 24, 24, 120),
      children: [
        // Header
        Text(
          AppLocalizations.of(context).quizTitle,
          style: GoogleFonts.instrumentSerif(
            fontSize: 28,
            fontWeight: FontWeight.w400,
            color: _textGhost,
          ),
        ),
        const SizedBox(height: 8),
        Text(
          AppLocalizations.of(context).quizSubtitle,
          style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
        ),
        const SizedBox(height: 24),

        // Progress
        _buildProgress(progress),
        const SizedBox(height: 24),

        // Question card
        _GlassCard(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                q.title,
                style: GoogleFonts.sora(
                  fontSize: 16,
                  fontWeight: FontWeight.w700,
                  color: _textGhost,
                ),
              ),
              if (q.maxSelections != null) ...[
                const SizedBox(height: 6),
                Text(
                  AppLocalizations.of(context).quizSelectUpTo(q.maxSelections ?? 0, selected.length, q.maxSelections ?? 0),
                  style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
                ),
              ] else if (!q.isRadio) ...[
                const SizedBox(height: 6),
                Text(
                  AppLocalizations.of(context).quizSelectAllThatApply,
                  style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
                ),
              ],
              const SizedBox(height: 20),

              // Options
              ...q.options.map((opt) {
                final isSelected = selected.contains(opt);
                final isDisabled = !q.isRadio &&
                    q.maxSelections != null &&
                    selected.length >= q.maxSelections! &&
                    !isSelected;

                return Padding(
                  padding: const EdgeInsets.only(bottom: 8),
                  child: _OptionTile(
                    label: opt,
                    selected: isSelected,
                    disabled: isDisabled,
                    isRadio: q.isRadio,
                    onTap: isDisabled
                        ? null
                        : () => _toggleOption(q.key, opt, q),
                  ),
                );
              }),
            ],
          ),
        ),

        if (_error != null) ...[
          const SizedBox(height: 16),
          Container(
            padding: const EdgeInsets.all(12),
            decoration: BoxDecoration(
              color: _expense.withValues(alpha: 0.1),
              borderRadius: BorderRadius.circular(10),
              border: Border.all(color: _expense.withValues(alpha: 0.3)),
            ),
            child: Text(_error!,
                style: GoogleFonts.sora(fontSize: 13, color: _expense)),
          ),
        ],

        const SizedBox(height: 24),

        // Navigation
        Row(
          children: [
            if (_currentQuestion > 0)
              Expanded(
                child: _GlassBtn(
                  label: 'Previous',
                  icon: Icons.arrow_back_rounded,
                  onTap: _prev,
                ),
              ),
            if (_currentQuestion > 0) const SizedBox(width: 12),
            Expanded(
              child: isLast
                  ? _SolidBtn(
                      label: _submitting ? 'Submitting...' : 'Complete Quiz',
                      onTap:
                          _hasCurrentAnswer && !_submitting ? _submit : null,
                    )
                  : _SolidBtn(
                      label: 'Next',
                      onTap: _hasCurrentAnswer ? _next : null,
                    ),
            ),
          ],
        ),
      ],
    );
  }

  Widget _buildProgress(double progress) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          mainAxisAlignment: MainAxisAlignment.spaceBetween,
          children: [
            Text(
              AppLocalizations.of(context).quizQuestionOf(_currentQuestion + 1, _questions.length),
              style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
            ),
            Text(
              '${(progress * 100).toInt()}%',
              style: GoogleFonts.firaCode(
                  fontSize: 12,
                  fontWeight: FontWeight.w600,
                  color: _accent),
            ),
          ],
        ),
        const SizedBox(height: 8),
        ClipRRect(
          borderRadius: BorderRadius.circular(4),
          child: LinearProgressIndicator(
            value: progress,
            minHeight: 4,
            backgroundColor: _cardBg,
            valueColor: const AlwaysStoppedAnimation<Color>(_accent),
          ),
        ),
      ],
    );
  }
}

// ═══════════════════════════════════════════════════════════
//  Shared Widgets
// ═══════════════════════════════════════════════════════════

class _OptionTile extends StatefulWidget {
  final String label;
  final bool selected;
  final bool disabled;
  final bool isRadio;
  final VoidCallback? onTap;

  const _OptionTile({
    required this.label,
    required this.selected,
    this.disabled = false,
    this.isRadio = false,
    this.onTap,
  });

  @override
  State<_OptionTile> createState() => _OptionTileState();
}

class _OptionTileState extends State<_OptionTile> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final bgColor = widget.selected
        ? _accent.withValues(alpha: 0.12)
        : _hovered
            ? _cardBg
            : Colors.transparent;
    final borderColor = widget.selected
        ? _accent.withValues(alpha: 0.4)
        : _cardBorder;
    final textColor = widget.disabled
        ? _textMuted.withValues(alpha: 0.4)
        : widget.selected
            ? _textGhost
            : _textMuted;

    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 150),
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
          decoration: BoxDecoration(
            color: bgColor,
            borderRadius: BorderRadius.circular(10),
            border: Border.all(color: borderColor),
          ),
          child: Row(
            children: [
              _buildIndicator(),
              const SizedBox(width: 12),
              Expanded(
                child: Text(
                  widget.label,
                  style: GoogleFonts.sora(
                    fontSize: 13,
                    fontWeight:
                        widget.selected ? FontWeight.w600 : FontWeight.w400,
                    color: textColor,
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildIndicator() {
    if (widget.isRadio) {
      return Container(
        width: 20,
        height: 20,
        decoration: BoxDecoration(
          shape: BoxShape.circle,
          border: Border.all(
            color: widget.selected ? _accent : _textMuted.withValues(alpha: 0.4),
            width: widget.selected ? 2 : 1.5,
          ),
        ),
        child: widget.selected
            ? Center(
                child: Container(
                  width: 10,
                  height: 10,
                  decoration: const BoxDecoration(
                    shape: BoxShape.circle,
                    color: _accent,
                  ),
                ),
              )
            : null,
      );
    }

    return Container(
      width: 20,
      height: 20,
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(4),
        color: widget.selected ? _accent : Colors.transparent,
        border: Border.all(
          color: widget.selected ? _accent : _textMuted.withValues(alpha: 0.4),
          width: widget.selected ? 0 : 1.5,
        ),
      ),
      child: widget.selected
          ? const Icon(Icons.check_rounded, size: 14, color: Colors.white)
          : null,
    );
  }
}

class _GlassCard extends StatelessWidget {
  final Widget child;
  const _GlassCard({required this.child});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(20),
      decoration: BoxDecoration(
        color: _cardBg,
        borderRadius: BorderRadius.circular(14),
        border: Border.all(color: _cardBorder),
        boxShadow: [
          BoxShadow(
            color: const Color(0xFF7B61FF).withValues(alpha: 0.03),
            blurRadius: 24,
            spreadRadius: 0,
          ),
        ],
      ),
      child: child,
    );
  }
}

class _GlassBtn extends StatefulWidget {
  final String label;
  final IconData? icon;
  final VoidCallback? onTap;

  const _GlassBtn({required this.label, this.icon, this.onTap});

  @override
  State<_GlassBtn> createState() => _GlassBtnState();
}

class _GlassBtnState extends State<_GlassBtn> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 150),
          padding: const EdgeInsets.symmetric(vertical: 14),
          decoration: BoxDecoration(
            color: _hovered ? _cardBg : Colors.transparent,
            borderRadius: BorderRadius.circular(10),
            border: Border.all(color: _cardBorder),
          ),
          child: Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              if (widget.icon != null) ...[
                Icon(widget.icon, size: 18, color: _textMuted),
                const SizedBox(width: 8),
              ],
              Text(
                widget.label,
                style: GoogleFonts.sora(
                  fontSize: 14,
                  fontWeight: FontWeight.w600,
                  color: _textGhost,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _SolidBtn extends StatefulWidget {
  final String label;
  final VoidCallback? onTap;

  const _SolidBtn({required this.label, this.onTap});

  @override
  State<_SolidBtn> createState() => _SolidBtnState();
}

class _SolidBtnState extends State<_SolidBtn> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final enabled = widget.onTap != null;
    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 150),
          padding: const EdgeInsets.symmetric(vertical: 14),
          decoration: BoxDecoration(
            color: enabled
                ? (_hovered
                    ? _accent.withValues(alpha: 0.9)
                    : _accent)
                : _accent.withValues(alpha: 0.3),
            borderRadius: BorderRadius.circular(10),
          ),
          child: Center(
            child: Text(
              widget.label,
              style: GoogleFonts.sora(
                fontSize: 14,
                fontWeight: FontWeight.w700,
                color: enabled
                    ? Colors.white
                    : Colors.white.withValues(alpha: 0.5),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
