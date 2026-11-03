import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:intl/intl.dart';
import 'package:provider/provider.dart';
import 'package:url_launcher/url_launcher.dart';
import '../l10n/generated/app_localizations.dart';
import '../models/billing_models.dart';
import '../providers/auth_provider.dart';
import '../services/analytics_service.dart';
import '../services/billing_service.dart';
import '../utils/formatters.dart';
import '../services/store_billing_service.dart';
import '../widgets/app_toast.dart';

// ── VaporClinic palette ─────────────────────────────────
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _cardBg = Color(0x0DFFFFFF);
const _cardBorder = Color(0x14FFFFFF);
const _income = Color(0xFF34D399);
const _expense = Color(0xFFF87171);
const _sheetBg = Color(0xFF0C0C18);
const _warning = Color(0xFFFBBF24);
const _blue = Color(0xFF60A5FA);

// ── Tier → Color mapping ────────────────────────────────
Color _tierColor(SubscriptionTier tier) => switch (tier) {
      SubscriptionTier.standard => _blue,
      SubscriptionTier.premium => _accent,
      SubscriptionTier.admin => _warning,
      _ => _textMuted,
    };

Color _statusColor(SubscriptionStatus status) => switch (status) {
      SubscriptionStatus.active => _income,
      SubscriptionStatus.trialing => _blue,
      SubscriptionStatus.pastDue => _warning,
      SubscriptionStatus.cancelled => _expense,
      SubscriptionStatus.expired => _expense,
      _ => _textMuted,
    };

// ═══════════════════════════════════════════════════════════
//  BillingPage
// ═══════════════════════════════════════════════════════════
class BillingPage extends StatefulWidget {
  const BillingPage({super.key});

  @override
  State<BillingPage> createState() => _BillingPageState();
}

class _BillingPageState extends State<BillingPage>
    with SingleTickerProviderStateMixin {
  bool _isLoading = true;
  String? _error;

  CurrentSubscription? _subscription;
  TrialStatus? _trialStatus;
  List<PricingPlan> _plans = [];
  List<BillingHistoryItem> _history = [];
  bool _showHistory = false;

  // In-app purchases (iOS). Prices come from the store, not from Stripe, because
  // the store price carries Apple's commission and App Review checks the two agree.
  Map<String, StorePlanPrice> _storePrices = const {};
  bool _storeBusy = false;
  bool get _useStore => storeBilling.isSupported && storeBilling.isReady;

  late AnimationController _fadeCtrl;
  late Animation<double> _fadeAnim;

  bool _handledReturn = false;

  @override
  void initState() {
    super.initState();
    _fadeCtrl = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 600),
    );
    _fadeAnim = CurvedAnimation(parent: _fadeCtrl, curve: Curves.easeOut);
    _fadeCtrl.forward();
    _loadAll();
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    if (_handledReturn) return;
    // Handle the return from hosted Stripe Checkout (/billing?checkout=success|cancel).
    final checkout = GoRouterState.of(context).uri.queryParameters['checkout'];
    if (checkout == null) return;
    _handledReturn = true;
    WidgetsBinding.instance.addPostFrameCallback((_) async {
      if (!mounted) return;
      if (checkout == 'success') {
        // The webhook may lag slightly; refresh profile (lifts any paywall) + reload.
        await context.read<AuthProvider>().refreshUser();
        if (mounted) await _loadAll();
        // Reliable post-payment signal: the subscription is only active once the
        // webhook has processed and _loadAll has refreshed _subscription. This is
        // the single business-critical conversion event for the revenue funnel.
        analytics.track(AnalyticsEvents.subscriptionStarted, {
          'tier': _subscription?.tier.name ?? 'unknown',
          'billing_cycle': _subscription?.interval.name ?? 'unknown',
        });
        if (mounted) {
          showAppToast(context, 'Payment received — your plan is being activated.',
              type: AppToastType.success);
        }
      } else if (checkout == 'cancel') {
        showAppToast(context, 'Checkout cancelled.');
      }
    });
  }

  @override
  void dispose() {
    _fadeCtrl.dispose();
    super.dispose();
  }

  // ── Data loading ──────────────────────────────────────

  Future<void> _loadAll() async {
    setState(() {
      _isLoading = true;
      _error = null;
    });
    try {
      final results = await Future.wait([
        billingService.getCurrentSubscription(),
        billingService.getTrialStatus(),
        billingService.getPlans(),
      ]);
      if (!mounted) return;
      setState(() {
        _subscription = results[0] as CurrentSubscription?;
        _trialStatus = results[1] as TrialStatus;
        _plans = results[2] as List<PricingPlan>;
        _isLoading = false;
      });
      if (_useStore) await _loadStorePrices();
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _isLoading = false;
        _error = 'Failed to load billing information';
      });
    }
  }

  Future<void> _loadStorePrices() async {
    try {
      final prices = await storeBilling.loadPrices();
      if (!mounted) return;
      setState(() => _storePrices = prices);
    } catch (_) {
      // Non-fatal: the buttons still open the store sheet, which shows the price.
    }
  }

  // ── Store purchases (iOS) ─────────────────────────────

  Future<void> _purchaseFromStore(PricingPlan plan, {required bool isYearly}) async {
    if (_storeBusy) return;
    final l10n = AppLocalizations.of(context);
    final productId =
        isYearly ? plan.storeProductIdYearly : plan.storeProductIdMonthly;
    var price = _storePrices[productId];
    if (price == null) {
      await _loadStorePrices();
      if (!mounted) return;
      price = _storePrices[productId];
    }
    if (price == null) {
      showAppToast(context, l10n.billStoreUnavailable, type: AppToastType.error);
      return;
    }
    final auth = context.read<AuthProvider>();
    setState(() => _storeBusy = true);
    try {
      final result = await storeBilling.purchase(price);
      if (!mounted) return;
      if (result == null) return; // user cancelled the store sheet
      analytics.track(AnalyticsEvents.subscriptionStarted, {
        'tier': plan.tier.toLowerCase(),
        'billing_cycle': isYearly ? 'yearly' : 'monthly',
        'provider': 'app_store',
      });
      showAppToast(context, l10n.billStorePurchaseDone, type: AppToastType.success);
      await auth.refreshUser();
      if (mounted) await _loadAll();
    } catch (_) {
      if (mounted) {
        showAppToast(context, l10n.billStorePurchaseFailed, type: AppToastType.error);
      }
    } finally {
      if (mounted) setState(() => _storeBusy = false);
    }
  }

  Future<void> _restorePurchases() async {
    if (_storeBusy) return;
    final l10n = AppLocalizations.of(context);
    final auth = context.read<AuthProvider>();
    setState(() => _storeBusy = true);
    try {
      final result = await storeBilling.restore();
      if (!mounted) return;
      final restored = result != null && result.provider.isStore && result.isActive;
      showAppToast(context, restored ? l10n.billRestoreDone : l10n.billRestoreNone);
      if (restored) {
        await auth.refreshUser();
        if (mounted) await _loadAll();
      }
    } catch (_) {
      if (mounted) {
        showAppToast(context, l10n.billStoreUnavailable, type: AppToastType.error);
      }
    } finally {
      if (mounted) setState(() => _storeBusy = false);
    }
  }

  Future<void> _manageInStore() async {
    try {
      final uri = await storeBilling.managementUrl();
      final launched = await launchUrl(uri, mode: LaunchMode.externalApplication);
      if (!launched) throw Exception('Could not open store subscriptions');
    } catch (_) {
      if (mounted) {
        showAppToast(context, AppLocalizations.of(context).billStoreUnavailable,
            type: AppToastType.error);
      }
    }
  }

  Future<void> _loadHistory() async {
    try {
      final history = await billingService.getBillingHistory();
      if (!mounted) return;
      setState(() => _history = history);
    } catch (_) {
      if (mounted) {
        showAppToast(context, 'Failed to load billing history',
            type: AppToastType.error);
      }
    }
  }

  // ── Actions ───────────────────────────────────────────

  Future<void> _cancelSubscription() async {
    final confirmed = await _confirmSheet(
      title: 'Cancel Subscription',
      message: _subscription?.currentPeriodEnd != null
          ? 'Are you sure? You\'ll keep access until ${DateFormat.yMMMd().format(_subscription!.currentPeriodEnd!)}.'
          : 'Are you sure you want to cancel your subscription?',
      confirmLabel: 'Yes, Cancel',
      confirmColor: _expense,
    );
    if (confirmed != true) return;
    try {
      await billingService.cancelSubscription();
      analytics.track(AnalyticsEvents.subscriptionCancelled);
      if (mounted) {
        showAppToast(context, 'Subscription cancelled', type: AppToastType.success);
      }
      _loadAll();
    } catch (_) {
      if (mounted) {
        showAppToast(context, 'Failed to cancel subscription',
            type: AppToastType.error);
      }
    }
  }

  /// Open the hosted Stripe Customer Portal (invoices, receipts, payment methods, cancel).
  Future<void> _openBillingPortal() async {
    try {
      final url = await billingService.createBillingPortalSession();
      if (url.isEmpty) throw Exception('No portal URL returned');
      final uri = Uri.parse(url);
      // Same tab on web so Stripe's return URL lands back in the app; external browser on mobile.
      final launched = kIsWeb
          ? await launchUrl(uri, webOnlyWindowName: '_self')
          : await launchUrl(uri, mode: LaunchMode.externalApplication);
      if (!launched) throw Exception('Could not open billing portal');
    } catch (_) {
      if (!mounted) return;
      showAppToast(context, 'Could not open billing management. Please try again.',
          type: AppToastType.error);
    }
  }

  Future<void> _startTrial() async {
    try {
      await billingService.startTrial();
      analytics.track(AnalyticsEvents.trialStarted);
      if (mounted) {
        showAppToast(context, 'Trial started!', type: AppToastType.success);
      }
      _loadAll();
    } catch (e) {
      if (mounted) {
        showAppToast(context, 'Failed to start trial', type: AppToastType.error);
      }
    }
  }

  void _openCheckout({
    required PricingPlan plan,
    required bool isYearly,
    bool isChange = false,
  }) {
    showModalBottomSheet(
      context: context,
      backgroundColor: _sheetBg,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (ctx) => _CheckoutSheet(
        plan: plan,
        isYearly: isYearly,
        isChangingPlan: isChange,
        currentSubscription: _subscription,
        onSuccess: () {
          Navigator.pop(ctx);
          _loadAll();
        },
        onCheckoutLaunched: () {
          Navigator.pop(ctx);
          if (mounted) {
            showAppToast(
              context,
              'Complete your payment in the page that opened — this will update once it\'s done.',
              duration: const Duration(seconds: 6),
            );
          }
        },
      ),
    );
  }

  // ── Helpers ───────────────────────────────────────────

  Future<bool?> _confirmSheet({
    required String title,
    required String message,
    String confirmLabel = 'Confirm',
    Color confirmColor = _expense,
  }) {
    return showModalBottomSheet<bool>(
      context: context,
      backgroundColor: _sheetBg,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (ctx) => Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            _sheetHandle(),
            const SizedBox(height: 20),
            Text(title,
                style: GoogleFonts.sora(
                    fontSize: 18,
                    fontWeight: FontWeight.w700,
                    color: _textGhost)),
            const SizedBox(height: 12),
            Text(message,
                textAlign: TextAlign.center,
                style: GoogleFonts.sora(fontSize: 14, color: _textMuted)),
            const SizedBox(height: 24),
            Row(
              children: [
                Expanded(
                  child: _GlassBtn(
                      label: 'Keep Subscription',
                      onTap: () => Navigator.pop(ctx, false)),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: _SolidBtn(
                    label: confirmLabel,
                    color: confirmColor,
                    onTap: () => Navigator.pop(ctx, true),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 8),
          ],
        ),
      ),
    );
  }

  // ── Build ─────────────────────────────────────────────

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.transparent,
      body: FadeTransition(
        opacity: _fadeAnim,
        child: _isLoading
            ? const Center(child: CircularProgressIndicator(color: _accent))
            : _error != null
                ? _buildError()
                : _buildContent(),
      ),
    );
  }

  Widget _buildError() {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(Icons.error_outline, size: 56,
              color: _expense.withValues(alpha: 0.7)),
          const SizedBox(height: 16),
          Text(_error!, style: GoogleFonts.sora(fontSize: 14, color: _textMuted)),
          const SizedBox(height: 20),
          _GlassBtn(label: 'Retry', onTap: _loadAll),
        ],
      ),
    );
  }

  Widget _buildContent() {
    return RefreshIndicator(
      color: _accent,
      backgroundColor: _sheetBg,
      onRefresh: _loadAll,
      child: ListView(
        padding: const EdgeInsets.fromLTRB(24, 24, 24, 120),
        children: [
          // Header
          Text(AppLocalizations.of(context).billPageTitle,
              style: GoogleFonts.instrumentSerif(
                  fontSize: 28,
                  fontWeight: FontWeight.w400,
                  color: _textGhost)),
          const SizedBox(height: 4),
          Text(AppLocalizations.of(context).billPageSubtitle,
              style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
          const SizedBox(height: 24),

          // Current subscription or no-subscription card
          if (_subscription != null) _buildCurrentSub() else _buildNoSub(),
          const SizedBox(height: 16),

          // Free trial card (only if no subscription & eligible)
          if (_subscription == null &&
              _trialStatus != null &&
              _trialStatus!.canStartTrial)
            ...[_buildTrialCard(), const SizedBox(height: 16)],

          // Plans
          ..._buildPlanCards(),
          if (_useStore) ...[
            const SizedBox(height: 8),
            Align(
              alignment: Alignment.centerRight,
              child: _SmallAction(
                label: AppLocalizations.of(context).billRestorePurchases,
                color: _textMuted,
                onTap: _storeBusy ? () {} : _restorePurchases,
              ),
            ),
          ],

          // History toggle
          const SizedBox(height: 16),
          _buildHistorySection(),

          // App Review 3.1.2: the purchase screen must carry functional links to the
          // Terms of Use and Privacy Policy, and state how auto-renewal works.
          const SizedBox(height: 20),
          _buildLegalFooter(),
        ],
      ),
    );
  }

  // ── Current subscription card ─────────────────────────

  Widget _buildCurrentSub() {
    final sub = _subscription!;

    if (sub.isTrial) return _buildTrialSub(sub);

    final tierColor = _tierColor(sub.tier);
    final statColor = _statusColor(sub.status);

    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(Icons.verified_outlined, size: 20, color: tierColor),
              const SizedBox(width: 10),
              Text(AppLocalizations.of(context).billCurrentPlan,
                  style: GoogleFonts.sora(
                      fontSize: 16,
                      fontWeight: FontWeight.w700,
                      color: _textGhost)),
              const Spacer(),
              _Badge(label: sub.tier.displayName, color: tierColor),
              const SizedBox(width: 8),
              _Badge(label: sub.status.displayName, color: statColor),
            ],
          ),
          const SizedBox(height: 16),

          Row(
            crossAxisAlignment: CrossAxisAlignment.end,
            children: [
              Text(
                // Stripe billing amounts are in the Stripe account currency (USD), never
                // the user's display currency — a CAD-display user still pays in USD.
                formatCurrencyCode(sub.amount, 'USD'),
                style: GoogleFonts.firaCode(
                    fontSize: 28, fontWeight: FontWeight.w700, color: _textGhost),
              ),
              const SizedBox(width: 4),
              Padding(
                padding: const EdgeInsets.only(bottom: 4),
                child: Text(
                  sub.interval == BillingCycle.yearly
                      ? AppLocalizations.of(context).billPerYear
                      : AppLocalizations.of(context).billPerMonth,
                  style: GoogleFonts.sora(fontSize: 14, color: _textMuted),
                ),
              ),
            ],
          ),
          const SizedBox(height: 12),

          if (sub.currentPeriodStart != null && sub.currentPeriodEnd != null)
            _InfoRow(
              label: 'Current period',
              value:
                  '${DateFormat.yMMMd().format(sub.currentPeriodStart!)} – ${DateFormat.yMMMd().format(sub.currentPeriodEnd!)}',
            ),

          if (sub.cancelAtPeriodEnd && sub.currentPeriodEnd != null) ...[
            const SizedBox(height: 12),
            Container(
              padding: const EdgeInsets.all(10),
              decoration: BoxDecoration(
                color: _warning.withValues(alpha: 0.08),
                borderRadius: BorderRadius.circular(8),
                border: Border.all(color: _warning.withValues(alpha: 0.2)),
              ),
              child: Row(
                children: [
                  Icon(Icons.warning_amber_rounded, size: 16, color: _warning),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      AppLocalizations.of(context).billWillCancelOn(
                          DateFormat.yMMMd().format(sub.currentPeriodEnd!)),
                      style: GoogleFonts.sora(fontSize: 12, color: _warning),
                    ),
                  ),
                ],
              ),
            ),
          ],

          // Past-due (payment failed) — graceful degradation: tell the user and route them
          // to the portal to fix their payment method rather than silently losing access.
          if (sub.status == SubscriptionStatus.pastDue) ...[
            const SizedBox(height: 12),
            Container(
              padding: const EdgeInsets.all(10),
              decoration: BoxDecoration(
                color: _expense.withValues(alpha: 0.08),
                borderRadius: BorderRadius.circular(8),
                border: Border.all(color: _expense.withValues(alpha: 0.25)),
              ),
              child: Row(
                children: [
                  Icon(Icons.error_outline, size: 16, color: _expense),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      AppLocalizations.of(context).billPastDueNotice,
                      style: GoogleFonts.sora(fontSize: 12, color: _expense),
                    ),
                  ),
                ],
              ),
            ),
          ],

          const SizedBox(height: 16),
          if (sub.provider.isStore)
            Row(
              children: [
                Expanded(
                  child: Text(
                    AppLocalizations.of(context)
                        .billBilledThroughStore(sub.provider.displayName),
                    style: GoogleFonts.sora(fontSize: 11, color: _textMuted),
                  ),
                ),
                // Cancel, payment method and receipts live in the store's own UI.
                _SmallAction(
                  label: AppLocalizations.of(context)
                      .billManageInStore(sub.provider.displayName),
                  color: _accent,
                  onTap: _manageInStore,
                ),
              ],
            )
          else
            Row(
              mainAxisAlignment: MainAxisAlignment.end,
              children: [
                // Stripe Customer Portal: invoices, receipts, payment methods, and cancel.
                _SmallAction(
                  label: 'Manage Billing',
                  color: _accent,
                  onTap: _openBillingPortal,
                ),
                if (sub.status == SubscriptionStatus.active && !sub.cancelAtPeriodEnd) ...[
                  const SizedBox(width: 12),
                  _SmallAction(
                    label: 'Cancel Subscription',
                    color: _expense,
                    onTap: _cancelSubscription,
                  ),
                ],
              ],
            ),
        ],
      ),
    );
  }

  Widget _buildTrialSub(CurrentSubscription sub) {
    final daysLeft = sub.daysRemaining ?? 0;
    final urgentColor = daysLeft <= 7 ? _warning : _blue;

    return Container(
      padding: const EdgeInsets.all(20),
      decoration: BoxDecoration(
        gradient: LinearGradient(
          colors: [
            _blue.withValues(alpha: 0.08),
            _accent.withValues(alpha: 0.06),
          ],
        ),
        borderRadius: BorderRadius.circular(14),
        border: Border.all(color: _blue.withValues(alpha: 0.25)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(Icons.rocket_launch_rounded, size: 20, color: _blue),
              const SizedBox(width: 10),
              Text(AppLocalizations.of(context).billPremiumTrial,
                  style: GoogleFonts.sora(
                      fontSize: 16,
                      fontWeight: FontWeight.w700,
                      color: _textGhost)),
              const Spacer(),
              _Badge(label: 'Trial', color: _blue),
            ],
          ),
          const SizedBox(height: 16),

          Row(
            crossAxisAlignment: CrossAxisAlignment.end,
            children: [
              Text(
                '$daysLeft',
                style: GoogleFonts.firaCode(
                    fontSize: 28, fontWeight: FontWeight.w700, color: urgentColor),
              ),
              const SizedBox(width: 6),
              Padding(
                padding: const EdgeInsets.only(bottom: 4),
                child: Text(
                  AppLocalizations.of(context).billDaysRemaining(daysLeft),
                  style: GoogleFonts.sora(fontSize: 14, color: _textMuted),
                ),
              ),
            ],
          ),
          const SizedBox(height: 12),

          if (sub.trialEnd != null)
            _InfoRow(
              label: 'Trial ends',
              value: DateFormat.yMMMd().format(sub.trialEnd!),
              valueColor: urgentColor,
            ),

          if (daysLeft <= 7) ...[
            const SizedBox(height: 12),
            Container(
              padding: const EdgeInsets.all(10),
              decoration: BoxDecoration(
                color: _warning.withValues(alpha: 0.08),
                borderRadius: BorderRadius.circular(8),
                border: Border.all(color: _warning.withValues(alpha: 0.2)),
              ),
              child: Row(
                children: [
                  Icon(Icons.info_outline, size: 16, color: _warning),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      AppLocalizations.of(context).billSubscribeToKeepAccess,
                      style: GoogleFonts.sora(fontSize: 12, color: _warning),
                    ),
                  ),
                ],
              ),
            ),
          ],
        ],
      ),
    );
  }

  // ── No subscription card ──────────────────────────────

  Widget _buildNoSub() {
    final trialExpired = _trialStatus != null &&
        !_trialStatus!.isTrialActive &&
        !_trialStatus!.canStartTrial;

    return _GlassCard(
      child: Column(
        children: [
          Icon(
            trialExpired ? Icons.timer_off_outlined : Icons.credit_card_off_outlined,
            size: 36,
            color: trialExpired ? _warning.withValues(alpha: 0.7) : _textMuted.withValues(alpha: 0.5),
          ),
          const SizedBox(height: 12),
          Text(
            trialExpired
                ? AppLocalizations.of(context).billTrialEnded
                : AppLocalizations.of(context).billNoActiveSubscription,
            style: GoogleFonts.sora(
                fontSize: 16,
                fontWeight: FontWeight.w700,
                color: _textGhost),
          ),
          const SizedBox(height: 6),
          Text(
            trialExpired
                ? AppLocalizations.of(context).billTrialEndedSubtitle
                : AppLocalizations.of(context).billNoSubSubtitle,
            textAlign: TextAlign.center,
            style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
          ),
        ],
      ),
    );
  }

  // ── Free trial card ───────────────────────────────────

  Widget _buildTrialCard() {
    return Container(
          padding: const EdgeInsets.all(20),
          decoration: BoxDecoration(
            gradient: LinearGradient(
              colors: [
                _blue.withValues(alpha: 0.08),
                _accent.withValues(alpha: 0.06),
              ],
            ),
            borderRadius: BorderRadius.circular(14),
            border: Border.all(color: _blue.withValues(alpha: 0.25)),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Icon(Icons.rocket_launch_rounded, size: 22, color: _blue),
                  const SizedBox(width: 10),
                  Text(AppLocalizations.of(context).billStartTrialTitle,
                      style: GoogleFonts.sora(
                          fontSize: 16,
                          fontWeight: FontWeight.w700,
                          color: _textGhost)),
                ],
              ),
              const SizedBox(height: 10),
              Text(
                AppLocalizations.of(context).billTrialDescription,
                style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
              ),
              const SizedBox(height: 16),
              _SolidBtn(
                label: 'Start Free Trial',
                color: _blue,
                onTap: _startTrial,
              ),
            ],
          ),
    );
  }

  // ── Plan cards ────────────────────────────────────────

  List<Widget> _buildPlanCards() {
    if (_plans.isEmpty) return [];
    return [
      Text(AppLocalizations.of(context).billChooseAPlan,
          style: GoogleFonts.sora(
              fontSize: 16, fontWeight: FontWeight.w700, color: _textGhost)),
      const SizedBox(height: 12),
      ..._plans.map((plan) => Padding(
            padding: const EdgeInsets.only(bottom: 12),
            child: _buildPlanCard(plan),
          )),
    ];
  }

  Widget _buildPlanCard(PricingPlan plan) {
    final tierEnum = SubscriptionTierExtension.fromString(plan.tier);
    final color = _tierColor(tierEnum);
    final hasPaidSub = _subscription != null && !_subscription!.isTrial;
    final isCurrentMonthly = hasPaidSub &&
        _subscription!.tier == tierEnum &&
        _subscription!.interval == BillingCycle.monthly;
    final isCurrentYearly = hasPaidSub &&
        _subscription!.tier == tierEnum &&
        _subscription!.interval == BillingCycle.yearly;
    // Only an existing *Stripe* subscription (paid, or a card-upfront trial) can be
    // changed in place. A no-card trial has no Stripe subscription (stripeSubscriptionId
    // is null), so it must go through Checkout to start paying — routing it to changePlan
    // would throw "Trial users must use the subscribe flow".
    final hasStripeSub = _subscription != null &&
        (_subscription!.stripeSubscriptionId?.isNotEmpty ?? false);
    final isChange = hasStripeSub &&
        (_subscription!.status == SubscriptionStatus.active ||
            _subscription!.status == SubscriptionStatus.trialing);

    return Container(
          padding: const EdgeInsets.all(20),
          decoration: BoxDecoration(
            color: _cardBg,
            borderRadius: BorderRadius.circular(14),
            border: Border.all(
                color: plan.isPopular
                    ? color.withValues(alpha: 0.3)
                    : _cardBorder),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              // Popular badge
              if (plan.isPopular)
                Container(
                  padding:
                      const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
                  decoration: BoxDecoration(
                    color: color.withValues(alpha: 0.15),
                    borderRadius: BorderRadius.circular(6),
                  ),
                  child: Text(AppLocalizations.of(context).billMostPopular,
                      style: GoogleFonts.sora(
                          fontSize: 10,
                          fontWeight: FontWeight.w700,
                          color: color,
                          letterSpacing: 1)),
                ),
              if (plan.isPopular) const SizedBox(height: 10),

              // Plan name + description
              Text(plan.name,
                  style: GoogleFonts.sora(
                      fontSize: 18,
                      fontWeight: FontWeight.w700,
                      color: _textGhost)),
              const SizedBox(height: 4),
              Text(plan.description,
                  style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
              const SizedBox(height: 14),

              // Pricing. On the store the quoted price is the store's, never Stripe's.
              Row(
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  Text(
                    _useStore
                        ? (_storePrices[plan.storeProductIdMonthly]?.priceString ??
                            AppLocalizations.of(context).billStorePriceLoading)
                        : formatCurrencyCode(plan.monthlyPrice, plan.currency),
                    style: GoogleFonts.firaCode(
                        fontSize: _useStore &&
                                _storePrices[plan.storeProductIdMonthly] == null
                            ? 14
                            : 26,
                        fontWeight: FontWeight.w700,
                        color: _textGhost),
                  ),
                  const SizedBox(width: 4),
                  Padding(
                    padding: const EdgeInsets.only(bottom: 3),
                    child: Text(AppLocalizations.of(context).billPerMonthSuffix,
                        style:
                            GoogleFonts.sora(fontSize: 13, color: _textMuted)),
                  ),
                ],
              ),
              if (_useStore && _storePrices[plan.storeProductIdYearly] != null) ...[
                const SizedBox(height: 4),
                Text(
                  [
                    _storePrices[plan.storeProductIdYearly]!.priceString,
                    AppLocalizations.of(context).billPerYear,
                  ].join(' '),
                  style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
                ),
              ] else if (!_useStore && plan.yearlyPrice > 0) ...[
                const SizedBox(height: 4),
                Text(
                  AppLocalizations.of(context).billYearlyAlt(
                      formatCurrencyCode(plan.yearlyPrice, plan.currency),
                      plan.yearlySavingsPercent),
                  style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
                ),
              ],
              const SizedBox(height: 14),

              // Features
              ...plan.features.map((f) => Padding(
                    padding: const EdgeInsets.only(bottom: 6),
                    child: Row(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Icon(Icons.check_circle_outline,
                            size: 16, color: _income),
                        const SizedBox(width: 8),
                        Expanded(
                            child: Text(f,
                                style: GoogleFonts.sora(
                                    fontSize: 12, color: _textGhost))),
                      ],
                    ),
                  )),
              const SizedBox(height: 16),

              // Buttons
              Row(
                children: [
                  Expanded(
                    child: _SolidBtn(
                      label: isCurrentMonthly
                          ? 'Current Plan'
                          : isChange
                              ? AppLocalizations.of(context).billSwitchToMonthly
                              : AppLocalizations.of(context).billSubscribeMonthly,
                      color: color,
                      onTap: isCurrentMonthly || _storeBusy
                          ? null
                          : _useStore
                              ? () => _purchaseFromStore(plan, isYearly: false)
                              : () => _openCheckout(
                                  plan: plan,
                                  isYearly: false,
                                  isChange: isChange),
                    ),
                  ),
                  if (plan.yearlyPrice > 0) ...[
                    const SizedBox(width: 8),
                    Expanded(
                      child: _GlassBtn(
                        label: isCurrentYearly
                            ? 'Current Plan'
                            : isChange || _useStore
                                ? (isChange
                                    ? AppLocalizations.of(context).billSwitchToYearly
                                    : AppLocalizations.of(context).billSubscribeYearly)
                                : 'Yearly (Save ${plan.yearlySavingsPercent}%)',
                        onTap: isCurrentYearly || _storeBusy
                            ? null
                            : _useStore
                                ? () => _purchaseFromStore(plan, isYearly: true)
                                : () => _openCheckout(
                                    plan: plan,
                                    isYearly: true,
                                    isChange: isChange),
                      ),
                    ),
                  ],
                ],
              ),
            ],
          ),
    );
  }

  // ── Legal footer ──────────────────────────────────────

  Widget _buildLegalFooter() {
    final l10n = AppLocalizations.of(context);
    final linkStyle = GoogleFonts.sora(
        fontSize: 12, fontWeight: FontWeight.w600, color: _accent);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          _useStore ? l10n.billAutoRenewNoticeStore : l10n.billAutoRenewNoticeWeb,
          style: GoogleFonts.sora(fontSize: 11, color: _textMuted, height: 1.4),
        ),
        const SizedBox(height: 8),
        Row(
          children: [
            GestureDetector(
              onTap: () => context.push('/terms'),
              behavior: HitTestBehavior.opaque,
              child: Text(l10n.regTermsOfService, style: linkStyle),
            ),
            const SizedBox(width: 14),
            GestureDetector(
              onTap: () => context.push('/privacy-policy'),
              behavior: HitTestBehavior.opaque,
              child: Text(l10n.regPrivacyPolicy, style: linkStyle),
            ),
          ],
        ),
      ],
    );
  }

  // ── Billing history ───────────────────────────────────

  Widget _buildHistorySection() {
    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          GestureDetector(
            onTap: () {
              setState(() => _showHistory = !_showHistory);
              if (_showHistory && _history.isEmpty) _loadHistory();
            },
            child: Row(
              children: [
                Icon(Icons.receipt_long_outlined, size: 20, color: _accent),
                const SizedBox(width: 10),
                Text(AppLocalizations.of(context).billBillingHistory,
                    style: GoogleFonts.sora(
                        fontSize: 16,
                        fontWeight: FontWeight.w700,
                        color: _textGhost)),
                const Spacer(),
                AnimatedRotation(
                  duration: const Duration(milliseconds: 200),
                  turns: _showHistory ? 0.5 : 0,
                  child: Icon(Icons.expand_more, color: _textMuted),
                ),
              ],
            ),
          ),
          if (_showHistory) ...[
            const SizedBox(height: 14),
            if (_history.isEmpty)
              Text(AppLocalizations.of(context).billNoHistoryYet,
                  style: GoogleFonts.sora(fontSize: 13, color: _textMuted))
            else
              ..._history.map((item) => Padding(
                    padding: const EdgeInsets.only(bottom: 8),
                    child: Row(
                      children: [
                        Icon(
                          item.status.toLowerCase() == 'paid'
                              ? Icons.check_circle_outline
                              : Icons.error_outline,
                          size: 16,
                          color: item.status.toLowerCase() == 'paid'
                              ? _income
                              : _expense,
                        ),
                        const SizedBox(width: 10),
                        Expanded(
                          child: Text(
                            DateFormat.yMMMd().format(item.paymentDate),
                            style:
                                GoogleFonts.sora(fontSize: 13, color: _textGhost),
                          ),
                        ),
                        Text(
                          formatCurrencyCode(item.amount, 'USD'),
                          style: GoogleFonts.firaCode(
                              fontSize: 13,
                              fontWeight: FontWeight.w600,
                              color: _textGhost),
                        ),
                        const SizedBox(width: 10),
                        _Badge(
                          label: item.status,
                          color: item.status.toLowerCase() == 'paid'
                              ? _income
                              : _expense,
                        ),
                      ],
                    ),
                  )),
          ],
        ],
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════
//  _CheckoutSheet — subscribe / change plan modal
// ═══════════════════════════════════════════════════════════
class _CheckoutSheet extends StatefulWidget {
  final PricingPlan plan;
  final bool isYearly;
  final bool isChangingPlan;
  final CurrentSubscription? currentSubscription;
  final VoidCallback onSuccess;
  // Called after a new-subscription Checkout page is opened (payment NOT yet complete).
  final VoidCallback onCheckoutLaunched;

  const _CheckoutSheet({
    required this.plan,
    required this.isYearly,
    required this.isChangingPlan,
    this.currentSubscription,
    required this.onSuccess,
    required this.onCheckoutLaunched,
  });

  @override
  State<_CheckoutSheet> createState() => _CheckoutSheetState();
}

class _CheckoutSheetState extends State<_CheckoutSheet> {
  final _promoCtrl = TextEditingController();
  bool _isSubmitting = false;
  String? _error;

  // Promo state
  PromoCodeResult? _promoResult;
  bool _promoLoading = false;

  double get _basePrice =>
      widget.isYearly ? widget.plan.yearlyPrice : widget.plan.monthlyPrice;

  double get _finalPrice =>
      _promoResult != null && _promoResult!.isValid
          ? _promoResult!.finalPrice / 100.0
          : _basePrice;

  String get _priceId =>
      widget.isYearly ? widget.plan.yearlyPriceId : widget.plan.monthlyPriceId;

  @override
  void dispose() {
    _promoCtrl.dispose();
    super.dispose();
  }

  Future<void> _applyPromo() async {
    final code = _promoCtrl.text.trim().toUpperCase();
    if (code.isEmpty) return;
    setState(() {
      _promoLoading = true;
      _promoResult = null;
    });
    try {
      final result = await billingService.validatePromoCode(
        promoCode: code,
        priceId: _priceId,
      );
      if (!mounted) return;
      setState(() {
        _promoResult = result;
        _promoLoading = false;
      });
      if (!result.isValid) {
        _showErr('Invalid or expired promo code');
      }
    } catch (_) {
      if (!mounted) return;
      setState(() => _promoLoading = false);
      _showErr('Failed to validate promo code');
    }
  }

  void _removePromo() {
    setState(() {
      _promoResult = null;
      _promoCtrl.clear();
    });
  }

  Future<void> _submit() async {
    setState(() {
      _isSubmitting = true;
      _error = null;
    });
    try {
      final tierInt = widget.plan.tier.toLowerCase() == 'premium' ? 2 : 1;
      final cycleInt = widget.isYearly ? 12 : 1;
      final promo = _promoResult?.isValid == true ? _promoCtrl.text.trim() : null;

      if (widget.isChangingPlan) {
        await billingService.changePlan(
          newTier: tierInt,
          newBillingCycle: cycleInt,
          promoCode: promo,
        );
        analytics.track(AnalyticsEvents.subscriptionUpgraded, {
          'tier': widget.plan.tier,
          'billing_cycle': widget.isYearly ? 'yearly' : 'monthly',
        });
        if (!mounted) return;
        widget.onSuccess();
        return;
      }

      // New subscription: open hosted Stripe Checkout. The subscription is activated
      // server-side via webhook only after the user actually pays — so we must NOT report
      // success here. We pass any pre-applied promo so Stripe honors it.
      // On iOS, request a Universal-Link return URL so completing hosted Checkout
      // in Safari deep-links back into the installed app (digital-goods link-out).
      // iOS builds with a RevenueCat key never reach this sheet (the store sheet
      // handles them); only a keyless iOS build could, and it keeps the
      // universal-link return so hosted Checkout deep-links back into the app.
      final isIos =
          !kIsWeb && defaultTargetPlatform == TargetPlatform.iOS;
      final url = await billingService.createCheckoutSession(
        tier: tierInt,
        billingCycle: cycleInt,
        promoCode: promo,
        platform: isIos ? 'ios' : null,
      );
      if (url.isEmpty) {
        throw Exception('No checkout URL returned');
      }
      final uri = Uri.parse(url);
      // On web, navigate the same tab so Stripe's return URL lands back in the app; on
      // mobile, open the external browser.
      final launched = kIsWeb
          ? await launchUrl(uri, webOnlyWindowName: '_self')
          : await launchUrl(uri, mode: LaunchMode.externalApplication);
      if (!launched) {
        throw Exception('Could not open checkout');
      }
      if (!mounted) return;
      widget.onCheckoutLaunched();
    } on DioException catch (e) {
      if (!mounted) return;
      final serverMsg = (e.response?.data is Map)
          ? (e.response!.data as Map)['message'] as String?
          : null;
      setState(() {
        _isSubmitting = false;
        _error = serverMsg ??
            (widget.isChangingPlan
                ? 'Could not change plan. Please try again.'
                : 'Could not start checkout. Please try again.');
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _isSubmitting = false;
        _error = widget.isChangingPlan
            ? 'Could not change plan. Please try again.'
            : 'Could not start checkout. Please try again.';
      });
    }
  }

  void _showErr(String msg) {
    showAppToast(context, msg, type: AppToastType.error);
  }

  @override
  Widget build(BuildContext context) {
    final tierColor =
        widget.plan.tier.toLowerCase() == 'premium' ? _accent : _blue;

    return DraggableScrollableSheet(
      initialChildSize: 0.7,
      maxChildSize: 0.9,
      minChildSize: 0.4,
      expand: false,
      builder: (ctx, scrollCtrl) {
        return ListView(
          controller: scrollCtrl,
          padding: const EdgeInsets.fromLTRB(24, 12, 24, 24),
          children: [
            // Handle + title
            Center(child: _sheetHandle()),
            const SizedBox(height: 16),
            Text(
              widget.isChangingPlan
                  ? 'Change Subscription Plan'
                  : 'Subscribe to ${widget.plan.name}',
              style: GoogleFonts.sora(
                  fontSize: 18,
                  fontWeight: FontWeight.w700,
                  color: _textGhost),
            ),
            const SizedBox(height: 20),

            // Plan change warning
            if (widget.isChangingPlan) ...[
              Container(
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(
                  color: _blue.withValues(alpha: 0.08),
                  borderRadius: BorderRadius.circular(10),
                  border: Border.all(color: _blue.withValues(alpha: 0.2)),
                ),
                child: Row(
                  children: [
                    Icon(Icons.info_outline, size: 18, color: _blue),
                    const SizedBox(width: 10),
                    Expanded(
                      child: Text(
                        AppLocalizations.of(context).billPlanReplacedProrated,
                        style:
                            GoogleFonts.sora(fontSize: 12, color: _textMuted),
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 16),
            ],

            // Current plan (if changing)
            if (widget.isChangingPlan &&
                widget.currentSubscription != null) ...[
              Row(
                children: [
                  Text(AppLocalizations.of(context).billCurrentLabel,
                      style:
                          GoogleFonts.sora(fontSize: 13, color: _textMuted)),
                  _Badge(
                    label:
                        '${widget.currentSubscription!.tier.displayName} – ${widget.currentSubscription!.interval.displayName}',
                    color: _textMuted,
                  ),
                ],
              ),
              const SizedBox(height: 8),
            ],

            // New plan summary
            Row(
              children: [
                Text(AppLocalizations.of(context).billNewPlanLabel,
                    style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
                _Badge(label: widget.plan.name, color: tierColor),
                const SizedBox(width: 8),
                _Badge(
                    label: widget.isYearly ? 'Yearly' : 'Monthly',
                    color: _textMuted),
              ],
            ),
            const SizedBox(height: 16),

            // Price
            Row(
              children: [
                if (_promoResult != null && _promoResult!.isValid) ...[
                  Text(
                    formatCurrencyCode(_basePrice, 'USD'),
                    style: GoogleFonts.firaCode(
                      fontSize: 18,
                      color: _textMuted,
                      decoration: TextDecoration.lineThrough,
                    ),
                  ),
                  const SizedBox(width: 10),
                ],
                Text(
                  formatCurrencyCode(_finalPrice, 'USD'),
                  style: GoogleFonts.firaCode(
                    fontSize: 22,
                    fontWeight: FontWeight.w700,
                    color: _promoResult != null && _promoResult!.isValid
                        ? _income
                        : _textGhost,
                  ),
                ),
                const SizedBox(width: 4),
                Text(
                  widget.isYearly ? AppLocalizations.of(context).billPerYearWord : AppLocalizations.of(context).billPerMonthWord,
                  style: GoogleFonts.sora(fontSize: 14, color: _textMuted),
                ),
              ],
            ),
            const SizedBox(height: 20),

            // Promo code section
            Text(AppLocalizations.of(context).billPromoCode,
                style: GoogleFonts.sora(
                    fontSize: 13,
                    fontWeight: FontWeight.w600,
                    color: _textGhost)),
            const SizedBox(height: 8),
            Row(
              children: [
                Expanded(
                  child: Container(
                    decoration: BoxDecoration(
                      color: _cardBg,
                      borderRadius: BorderRadius.circular(10),
                      border: Border.all(color: _cardBorder),
                    ),
                    child: TextField(
                      controller: _promoCtrl,
                      style:
                          GoogleFonts.firaCode(fontSize: 14, color: _textGhost),
                      textCapitalization: TextCapitalization.characters,
                      decoration: InputDecoration(
                        hintText: 'Enter promo code',
                        hintStyle:
                            GoogleFonts.sora(fontSize: 13, color: _textMuted),
                        border: InputBorder.none,
                        contentPadding: const EdgeInsets.symmetric(
                            horizontal: 12, vertical: 12),
                      ),
                    ),
                  ),
                ),
                const SizedBox(width: 8),
                if (_promoResult != null && _promoResult!.isValid)
                  _SmallAction(
                      label: 'Remove', color: _expense, onTap: _removePromo)
                else
                  _SolidBtn(
                    label: _promoLoading ? '...' : 'Apply',
                    color: _accent,
                    onTap: _promoLoading ? null : _applyPromo,
                  ),
              ],
            ),

            // Promo success
            if (_promoResult != null && _promoResult!.isValid) ...[
              const SizedBox(height: 8),
              Row(
                children: [
                  Icon(Icons.check_circle, size: 16, color: _income),
                  const SizedBox(width: 6),
                  Expanded(
                    child: Text(
                      _promoResult!.description ??
                          'Promo applied! Save ${formatCurrencyCode(_promoResult!.discountAmount / 100.0, 'USD')}',
                      style:
                          GoogleFonts.sora(fontSize: 12, color: _income),
                    ),
                  ),
                ],
              ),
            ],

            // Error
            if (_error != null) ...[
              const SizedBox(height: 12),
              Container(
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(
                  color: _expense.withValues(alpha: 0.08),
                  borderRadius: BorderRadius.circular(8),
                  border: Border.all(color: _expense.withValues(alpha: 0.2)),
                ),
                child: Text(_error!,
                    style: GoogleFonts.sora(fontSize: 12, color: _expense)),
              ),
            ],

            const SizedBox(height: 16),

            // Footer note
            Text(
              widget.isChangingPlan
                  ? 'Your plan will be changed immediately and you will be charged a prorated amount.'
                  : 'Your subscription will start immediately. You can cancel anytime from your profile.',
              style: GoogleFonts.sora(fontSize: 11, color: _textMuted),
            ),
            const SizedBox(height: 20),

            // Action buttons
            Row(
              children: [
                Expanded(
                  child: _GlassBtn(
                    label: 'Cancel',
                    onTap:
                        _isSubmitting ? null : () => Navigator.pop(context),
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: _SolidBtn(
                    label: _isSubmitting
                        ? 'Processing...'
                        : widget.isChangingPlan
                            ? 'Change Plan'
                            : 'Subscribe Now',
                    color: tierColor,
                    onTap: _isSubmitting ? null : _submit,
                  ),
                ),
              ],
            ),
          ],
        );
      },
    );
  }
}

// ═══════════════════════════════════════════════════════════
//  Shared cinematic widgets
// ═══════════════════════════════════════════════════════════

Widget _sheetHandle() => Container(
      width: 40,
      height: 4,
      decoration: BoxDecoration(
        color: _textMuted.withValues(alpha: 0.3),
        borderRadius: BorderRadius.circular(2),
      ),
    );

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

class _Badge extends StatelessWidget {
  final String label;
  final Color color;
  const _Badge({required this.label, required this.color});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.12),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Text(label,
          style: GoogleFonts.sora(
              fontSize: 11, fontWeight: FontWeight.w600, color: color)),
    );
  }
}

class _InfoRow extends StatelessWidget {
  final String label;
  final String value;
  final Color? valueColor;
  const _InfoRow({required this.label, required this.value, this.valueColor});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 4),
      child: Row(
        children: [
          Text('$label: ',
              style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
          Expanded(
            child: Text(value,
                style: GoogleFonts.sora(
                    fontSize: 13, color: valueColor ?? _textGhost)),
          ),
        ],
      ),
    );
  }
}

class _GlassBtn extends StatefulWidget {
  final String label;
  final VoidCallback? onTap;
  const _GlassBtn({required this.label, this.onTap});

  @override
  State<_GlassBtn> createState() => _GlassBtnState();
}

class _GlassBtnState extends State<_GlassBtn> {
  bool _h = false;

  @override
  Widget build(BuildContext context) {
    return MouseRegion(
      onEnter: (_) => setState(() => _h = true),
      onExit: (_) => setState(() => _h = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 150),
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
          decoration: BoxDecoration(
            color: _h ? _accent.withValues(alpha: 0.12) : _cardBg,
            borderRadius: BorderRadius.circular(10),
            border: Border.all(
                color: _h ? _accent.withValues(alpha: 0.4) : _cardBorder),
          ),
          child: Center(
            child: Text(widget.label,
                style: GoogleFonts.sora(
                    fontSize: 13,
                    fontWeight: FontWeight.w600,
                    color: widget.onTap == null
                        ? _textMuted.withValues(alpha: 0.5)
                        : _accent)),
          ),
        ),
      ),
    );
  }
}

class _SolidBtn extends StatefulWidget {
  final String label;
  final Color color;
  final VoidCallback? onTap;
  const _SolidBtn({required this.label, required this.color, this.onTap});

  @override
  State<_SolidBtn> createState() => _SolidBtnState();
}

class _SolidBtnState extends State<_SolidBtn> {
  bool _h = false;

  @override
  Widget build(BuildContext context) {
    final disabled = widget.onTap == null;
    return MouseRegion(
      onEnter: (_) => setState(() => _h = true),
      onExit: (_) => setState(() => _h = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 150),
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
          decoration: BoxDecoration(
            color: disabled
                ? _textMuted.withValues(alpha: 0.15)
                : _h
                    ? widget.color.withValues(alpha: 0.9)
                    : widget.color.withValues(alpha: 0.75),
            borderRadius: BorderRadius.circular(10),
          ),
          child: Center(
            child: Text(widget.label,
                style: GoogleFonts.sora(
                    fontSize: 13,
                    fontWeight: FontWeight.w600,
                    color: disabled ? _textMuted : Colors.white)),
          ),
        ),
      ),
    );
  }
}

class _SmallAction extends StatefulWidget {
  final String label;
  final Color? color;
  final VoidCallback? onTap;
  const _SmallAction({required this.label, this.color, this.onTap});

  @override
  State<_SmallAction> createState() => _SmallActionState();
}

class _SmallActionState extends State<_SmallAction> {
  bool _h = false;

  @override
  Widget build(BuildContext context) {
    final c = widget.color ?? _accent;
    return MouseRegion(
      onEnter: (_) => setState(() => _h = true),
      onExit: (_) => setState(() => _h = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: Text(widget.label,
            style: GoogleFonts.sora(
                fontSize: 12,
                fontWeight: FontWeight.w600,
                color: _h ? c : c.withValues(alpha: 0.7),
                decoration: _h ? TextDecoration.underline : null,
                decorationColor: c)),
      ),
    );
  }
}
