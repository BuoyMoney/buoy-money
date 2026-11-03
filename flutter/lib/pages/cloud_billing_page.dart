import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:url_launcher/url_launcher.dart';

import '../config/server_capabilities.dart';
import '../l10n/generated/app_localizations.dart';
import '../services/ai_status_service.dart';
import '../services/ai_usage_service.dart';
import '../services/cloud_service.dart';
import '../utils/api_error.dart';
import '../utils/formatters.dart';
import '../widgets/app_toast.dart';
import '../widgets/cinematic/glass_settings.dart';

/// One row of the bill: [label] says what it is, [detail] how it is priced, [amountCents] what it adds.
class BillingLine {
  final String kind;
  final String label;
  final String? detail;
  final int amountCents;
  final String currency;

  const BillingLine({
    required this.kind,
    required this.label,
    required this.amountCents,
    required this.currency,
    this.detail,
  });
}

/// US-style money from whole cents ("$23.99", "-$1.50"). Division happens once, at display time.
String formatCents(int cents, String currency) =>
    formatCurrencyCode(cents / 100, currency.toUpperCase());

/// The next invoice as rows: the products on the plan (with unit price x quantity), then anything metered or one-off.
/// A refresh overage row appears only when it has an amount, and instant refresh itself is never listed as an offer.
/// With no invoice preview (the billing service could not give one) the plan's own items stand in, at their list price.
List<BillingLine> billingLines(
  CloudBillingSummary summary,
  AppLocalizations l10n,
) {
  final interval =
      summary.upcoming?.interval ?? summary.subscription?.interval ?? 'monthly';
  final per = interval == 'yearly'
      ? l10n.cloudProductsPerYear
      : l10n.cloudProductsPerMonth;
  final lines = <BillingLine>[];

  String productLabel(String? product, String fallback) => switch (product) {
    'bank_sync' => l10n.cloudBillProductBankSync,
    'ai' => l10n.cloudBillProductAi,
    'bank_sync_ai_bundle' => l10n.cloudBillProductBundle,
    'extra_logins' => l10n.cloudBillProductExtraLogins,
    'ai_overage' => l10n.cloudBillProductAiPayg,
    _ => fallback,
  };

  String priced(int quantity, int unitCents, String currency) {
    final unit = formatCents(unitCents, currency);
    return quantity > 1
        ? l10n.cloudBillQtyTimes(quantity, unit, per)
        : l10n.cloudBillPerInterval(unit, per);
  }

  final upcoming = summary.upcoming;
  if (upcoming != null) {
    for (final l in upcoming.lines) {
      switch (l.kind) {
        case 'product':
          lines.add(
            BillingLine(
              kind: 'product',
              label: productLabel(l.product, l.description),
              detail: l.proration
                  ? l.description
                  : l.unitAmountCents > 0
                  ? priced(l.quantity, l.unitAmountCents, upcoming.currency)
                  : null,
              amountCents: l.amountCents,
              currency: upcoming.currency,
            ),
          );
        case 'ai_overage':
          lines.add(
            BillingLine(
              kind: 'ai_overage',
              label: l10n.cloudBillProductAiPayg,
              detail: l10n.cloudBillPaygSoFar,
              amountCents: l.amountCents,
              currency: upcoming.currency,
            ),
          );
        case 'refresh_overage':
          // The feature is off, so this is normally absent. Hidden at zero in any case.
          if (l.amountCents != 0) {
            lines.add(
              BillingLine(
                kind: 'refresh_overage',
                label: l10n.cloudBillRefreshOverage,
                detail: l.description,
                amountCents: l.amountCents,
                currency: upcoming.currency,
              ),
            );
          }
        default:
          if (l.amountCents != 0) {
            lines.add(
              BillingLine(
                kind: 'other',
                label: l.description,
                amountCents: l.amountCents,
                currency: upcoming.currency,
              ),
            );
          }
      }
    }
    return lines;
  }

  final sub = summary.subscription;
  if (sub != null) {
    for (final i in sub.items) {
      if (i.product == 'ai_overage') {
        continue; // metered: no price until there is usage
      }
      lines.add(
        BillingLine(
          kind: 'product',
          label: productLabel(i.product, i.product),
          detail: priced(i.quantity, i.unitAmountCents, i.currency),
          amountCents: i.unitAmountCents * i.quantity,
          currency: i.currency,
        ),
      );
    }
  }
  return lines;
}

/// Settings > BuoyAI Cloud > Billing. What the customer pays, line by line, for both editions: each product on the
/// plan with its price and interval, extra bank logins as quantity x unit price, AI spend against the allowance (and
/// pay-as-you-go spend against its cap), extra refreshes only when there are some, the next invoice date and estimated
/// total, and a button to Stripe's page for the card and invoices. The hosted service reads its own user's Cloud
/// customer; a self-hosted server asks the Cloud through its pairing. Neither sends a customer id.
class CloudBillingPage extends StatefulWidget {
  const CloudBillingPage({
    super.key,
    this.service,
    this.aiStatusLoader,
    this.aiUsageLoader,
    this.open,
    this.portal = false,
    this.animateBackground = true,
  });

  /// False in widget tests: the portal's animated background never settles.
  final bool animateBackground;

  /// The Cloud portal's billing page (buoy-ai.com/cloud/billing): a top-level page with its own background, whose
  /// way back is the portal home, and with no links into the hosted app.
  final bool portal;

  /// Injected in tests; the app uses the global service.
  final CloudService? service;
  final Future<AiStatus> Function()? aiStatusLoader;
  final Future<AiUsageSummary> Function()? aiUsageLoader;

  /// Opens a web address (the Stripe page). Injected in tests.
  final Future<void> Function(String url)? open;

  @override
  State<CloudBillingPage> createState() => _CloudBillingPageState();
}

enum _Problem {
  none,
  forbidden,
  awaitingConfirmation,
  unavailable,

  /// A self-hosted server that is not linked to the Cloud yet: an expected state with a next step, not an error.
  notLinked,
  failed,
}

/// True when the server answered "this server is not linked to the Cloud" (`not_paired`).
bool _isNotLinked(Object error) => CloudService.codeOf(error) == 'not_paired';

class _CloudBillingPageState extends State<CloudBillingPage> {
  bool _loading = true;
  _Problem _problem = _Problem.none;
  CloudBillingSummary? _summary;
  AiStatus? _aiStatus;
  AiUsageSummary? _usage;
  CloudPlan? _hostedPlan;
  bool _opening = false;

  CloudService get _service => widget.service ?? cloudService;
  bool get _selfHost => ServerEdition.current.value.isSelfHost;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _problem = _Problem.none;
    });

    CloudBillingSummary? summary;
    var problem = _Problem.none;
    try {
      summary = await _service.billingSummary(selfHost: _selfHost);
      if (summary == null) problem = _Problem.unavailable;
    } catch (e) {
      if (_isNotLinked(e)) {
        problem = _Problem.notLinked;
      } else if (CloudService.statusOf(e) != 403) {
        problem = _Problem.failed;
      } else if (CloudService.codeOf(e) == 'awaiting_owner_confirmation') {
        // The pairing is not confirmed yet: the fix is the email, not a different account.
        problem = _Problem.awaitingConfirmation;
      } else {
        problem = _Problem.forbidden;
      }
    }

    // The rest are courtesies: a missing one hides its card, it does not fail the page.
    AiStatus? status;
    AiUsageSummary? usage;
    CloudPlan? plan;
    if (problem == _Problem.none) {
      try {
        status =
            await (widget.aiStatusLoader ?? () => aiStatusService.fetch())();
        if (status.enabled) {
          usage =
              await (widget.aiUsageLoader ?? () => AiUsageService().fetch())();
        }
      } catch (_) {}
      if (!_selfHost) {
        try {
          plan = await _service.plan();
        } catch (_) {}
      }
    }

    if (!mounted) return;
    setState(() {
      _summary = summary;
      _problem = problem;
      _aiStatus = status;
      _usage = usage;
      _hostedPlan = plan;
      _loading = false;
    });
  }

  Future<void> _openPortal() async {
    final l10n = AppLocalizations.of(context);
    if (_opening) return;
    setState(() => _opening = true);
    try {
      final url = await _service.billingPortal(
          selfHost: _selfHost, fromPortal: widget.portal);
      if (url.isEmpty) throw StateError('no billing address');
      await (widget.open ?? _openExternal)(url);
    } catch (e) {
      if (!mounted) return;
      if (_isNotLinked(e)) {
        setState(() => _problem = _Problem.notLinked);
        return;
      }
      showAppToast(
        context,
        apiErrorMessage(e, fallback: l10n.cloudBillManageFailed),
        type: AppToastType.error,
      );
    } finally {
      if (mounted) setState(() => _opening = false);
    }
  }

  static Future<void> _openExternal(String url) async {
    final uri = Uri.tryParse(url);
    if (uri != null && uri.scheme == 'https') {
      await launchUrl(uri, mode: LaunchMode.externalApplication);
    }
  }

  String _date(DateTime d) =>
      MaterialLocalizations.of(context).formatShortDate(d);

  void _goLink() {
    // Pushed from the Cloud page: going back lands on its link card. Opened directly: go there.
    if (context.canPop()) {
      context.pop();
    } else {
      context.go(_home);
    }
  }

  /// Where "back" and "see add-ons" lead: the Cloud page, or the portal home.
  String get _home => widget.portal ? '/' : '/settings/cloud';

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    return GlassSubPage(
      title: l10n.cloudBillTitle,
      intro: l10n.cloudBillCardBody,
      fallbackPath: _home,
      backdrop: widget.portal,
      animateBackdrop: widget.animateBackground,
      children: [
        if (_loading)
          const Padding(
            padding: EdgeInsets.all(24),
            child: GlassSpinner(),
          )
        else
          ..._content(context),
      ],
    );
  }

  List<Widget> _content(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final summary = _summary;

    switch (_problem) {
      case _Problem.forbidden:
        return [_message(l10n.ownerManagesThis, key: 'cloud-bill-forbidden')];
      case _Problem.awaitingConfirmation:
        return [
          _message(l10n.cloudPairAwaiting,
              key: 'cloud-bill-awaiting', tone: GlassNoteTone.warning),
        ];
      case _Problem.unavailable:
        return [
          _message(l10n.cloudBillUnavailable, key: 'cloud-bill-unavailable'),
        ];
      case _Problem.notLinked:
        return [
          GlassSection(
            key: const ValueKey('cloud-bill-not-linked'),
            icon: Icons.link_rounded,
            title: l10n.cloudPairTitle,
            highlight: true,
            children: [
              GlassNote(text: l10n.cloudBillNotLinked),
              const SizedBox(height: 14),
              GlassPrimaryButton(
                key: const ValueKey('cloud-bill-link'),
                expand: true,
                icon: Icons.link_rounded,
                onPressed: _goLink,
                label: l10n.cloudLinkStart,
              ),
            ],
          ),
        ];
      case _Problem.failed:
        return [_failed(l10n.cloudLoadFailed)];
      case _Problem.none:
        break;
    }

    // The billing service could not be asked at all: say so rather than "no plan".
    if (summary == null ||
        (summary.subscription == null && summary.lookupFailed)) {
      return [_failed(l10n.cloudLoadFailed)];
    }

    if (summary.subscription == null) {
      return [
        _SectionCard(
          key: const ValueKey('cloud-bill-empty'),
          icon: Icons.receipt_long_rounded,
          title: l10n.cloudBillEmptyTitle,
          children: [
            Text(l10n.cloudBillEmptyBody, style: cineBodyStyle()),
            if (!_selfHost) ...[
              const SizedBox(height: 14),
              Align(
                alignment: Alignment.centerLeft,
                child: GlassPrimaryButton(
                  key: const ValueKey('cloud-bill-addons'),
                  onPressed: () => context.canPop()
                      ? context.pop()
                      : context.go(_home),
                  label: l10n.cloudBillSeeAddons,
                ),
              ),
            ],
          ],
        ),
        ..._hostedPlanLink(l10n),
      ];
    }

    final sub = summary.subscription!;
    final lines = billingLines(summary, l10n);
    final planLines = lines.where((l) => l.kind == 'product').toList();
    final usageLines = lines.where((l) => l.kind != 'product').toList();
    final upcoming = summary.upcoming;
    final currency =
        upcoming?.currency ??
        (sub.items.isEmpty ? 'usd' : sub.items.first.currency);

    return [
      _SectionCard(
        key: const ValueKey('cloud-bill-plan'),
        icon: Icons.workspace_premium_outlined,
        title: l10n.cloudBillPlanTitle,
        trailing: _Badge(
          key: ValueKey(
            sub.founding ? 'cloud-bill-founding' : 'cloud-bill-list',
          ),
          label: sub.founding ? l10n.cloudBillFounding : l10n.cloudBillList,
          color: sub.founding ? cineIncome : cineMuted,
        ),
        children: [
          Text(
            _statusText(l10n, sub.status),
            key: const ValueKey('cloud-bill-status'),
            style: cineBodyStyle(
              color: sub.status == 'past_due' ? cineExpense : null,
            ),
          ),
          if (sub.cancelAtPeriodEnd && sub.currentPeriodEnd != null)
            Padding(
              padding: const EdgeInsets.only(top: 4),
              child: Text(
                l10n.cloudBillEnds(_date(sub.currentPeriodEnd!)),
                key: const ValueKey('cloud-bill-ends'),
                style: cineBodyStyle(),
              ),
            ),
          const SizedBox(height: 8),
          for (final line in planLines) _LineRow(line: line),
          if (sub.founding)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Text(l10n.cloudBillFoundingNote, style: cineMutedStyle()),
            ),
        ],
      ),
      if (usageLines.isNotEmpty)
        _SectionCard(
          key: const ValueKey('cloud-bill-usage-charges'),
          icon: Icons.bolt_rounded,
          title: l10n.cloudBillUsageChargesTitle,
          children: [for (final line in usageLines) _LineRow(line: line)],
        ),
      ..._aiCard(l10n),
      _SectionCard(
        key: const ValueKey('cloud-bill-next'),
        icon: Icons.event_outlined,
        title: l10n.cloudBillNextTitle,
        children: [
          if (upcoming == null)
            Text(
              l10n.cloudBillNoInvoice,
              key: const ValueKey('cloud-bill-no-invoice'),
              style: cineMutedStyle(size: 13),
            )
          else ...[
            if (upcoming.nextInvoiceAt != null || sub.currentPeriodEnd != null)
              Text(
                l10n.cloudBillNextOn(
                  _date((upcoming.nextInvoiceAt ?? sub.currentPeriodEnd)!),
                ),
                key: const ValueKey('cloud-bill-next-date'),
                style: cineBodyStyle(),
              ),
            const SizedBox(height: 10),
            Row(
              children: [
                Expanded(
                  child: Text(
                    l10n.cloudBillEstimatedTotal,
                    style: cineRowTitleStyle(),
                  ),
                ),
                Text(
                  formatCents(upcoming.totalCents, currency),
                  key: const ValueKey('cloud-bill-total'),
                  style: GoogleFonts.firaCode(
                    fontSize: 18,
                    fontWeight: FontWeight.w600,
                    color: cineText,
                  ),
                ),
              ],
            ),
            const SizedBox(height: 4),
            Text(l10n.cloudBillEstimateNote, style: cineMutedStyle()),
          ],
        ],
      ),
      GlassPrimaryButton(
        key: const ValueKey('cloud-bill-manage'),
        expand: true,
        onPressed: _opening ? null : _openPortal,
        icon: Icons.open_in_new_rounded,
        label: l10n.cloudBillManage,
      ),
      ..._hostedPlanLink(l10n),
    ];
  }

  Widget _message(String text,
          {required String key, GlassNoteTone tone = GlassNoteTone.info}) =>
      GlassNote(key: ValueKey(key), text: text, tone: tone);

  Widget _failed(String text) {
    final l10n = AppLocalizations.of(context);
    return Column(
      key: const ValueKey('cloud-bill-failed'),
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        GlassNote(text: text, tone: GlassNoteTone.error),
        const SizedBox(height: 12),
        GlassSecondaryButton(
          key: const ValueKey('cloud-bill-retry'),
          onPressed: _load,
          label: l10n.cloudBillRetry,
        ),
      ],
    );
  }

  /// A hosted user who also holds a Standard or Premium plan keeps that billing page; point to it.
  List<Widget> _hostedPlanLink(AppLocalizations l10n) {
    if (_selfHost || widget.portal || _hostedPlan?.hasLivePlan != true) {
      return const [];
    }
    return [
      const SizedBox(height: 16),
      Text(
        l10n.cloudBillHostedPlan,
        key: const ValueKey('cloud-bill-hosted-plan'),
        style: cineMutedStyle(),
      ),
      Align(
        alignment: Alignment.centerLeft,
        child: GlassTextAction(
          key: const ValueKey('cloud-bill-open-billing'),
          onPressed: () => context.push('/billing'),
          label: l10n.cloudBillHostedPlanOpen,
        ),
      ),
    ];
  }

  String _statusText(AppLocalizations l10n, String status) => switch (status) {
    'active' => l10n.cloudBillStatusActive,
    'trialing' => l10n.cloudBillStatusTrialing,
    'past_due' => l10n.cloudBillStatusPastDue,
    _ => l10n.cloudBillStatusOther(status),
  };

  /// AI spend against the allowance, and pay-as-you-go spend against its cap. Absent when AI is off or the meter is unavailable.
  List<Widget> _aiCard(AppLocalizations l10n) {
    final usage = _usage;
    if (_aiStatus?.enabled != true || usage == null) return const [];
    final payg = usage.payg;
    return [
      _SectionCard(
        key: const ValueKey('cloud-bill-ai'),
        icon: Icons.auto_awesome_rounded,
        title: l10n.cloudBillUsageTitle,
        children: [
          if (!usage.unlimited)
            ClipRRect(
              borderRadius: BorderRadius.circular(6),
              child: LinearProgressIndicator(
                key: const ValueKey('cloud-bill-ai-meter'),
                minHeight: 8,
                value: usage.percent.clamp(0, 100) / 100,
                backgroundColor: cineCardBorder,
                color: usage.isOver
                    ? cineExpense
                    : usage.isWarning
                    ? cineWarning
                    : cineAccent,
              ),
            ),
          const SizedBox(height: 6),
          Text(
            usage.unlimited
                ? l10n.cloudAiMeterUnlimited(formatMicros(usage.usedMicros))
                : l10n.cloudBillUsageUsed(
                    formatMicros(usage.usedMicros),
                    formatMicros(usage.allowanceMicros),
                  ),
            key: const ValueKey('cloud-bill-ai-used'),
            style: cineBodyStyle(),
          ),
          Text(
            l10n.cloudBillUsageResets(_date(usage.periodEnd)),
            style: cineMutedStyle(),
          ),
          if (payg.available) ...[
            const SizedBox(height: 8),
            Text(
              payg.enabled
                  ? l10n.cloudBillPaygOn(
                      formatMicros(payg.usedMicros),
                      formatMicros(payg.capMicros),
                    )
                  : l10n.cloudBillPaygOff,
              key: ValueKey(
                payg.enabled ? 'cloud-bill-payg-on' : 'cloud-bill-payg-off',
              ),
              style: cineBodyStyle(),
            ),
          ],
          // The AI usage page lives in the hosted app; the portal has no link into it.
          if (!widget.portal)
          Align(
            alignment: Alignment.centerLeft,
            child: GlassTextAction(
              key: const ValueKey('cloud-bill-ai-usage'),
              onPressed: () => context.push('/settings/ai-usage'),
              label: l10n.cloudAiManage,
            ),
          ),
        ],
      ),
    ];
  }
}

class _SectionCard extends StatelessWidget {
  const _SectionCard({
    required this.title,
    required this.children,
    required this.icon,
    this.trailing,
    super.key,
  });

  final String title;
  final IconData icon;
  final Widget? trailing;
  final List<Widget> children;

  @override
  Widget build(BuildContext context) => GlassSection(
    icon: icon,
    title: title,
    trailing: trailing,
    children: children,
  );
}

/// A small pill on a card header ("Founding price").
class _Badge extends StatelessWidget {
  const _Badge({required this.label, required this.color, super.key});

  final String label;
  final Color color;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 3),
    decoration: BoxDecoration(
      color: color.withValues(alpha: 0.15),
      borderRadius: BorderRadius.circular(8),
      border: Border.all(color: color.withValues(alpha: 0.3)),
    ),
    child: Text(
      label,
      style: GoogleFonts.sora(
        fontSize: 11,
        fontWeight: FontWeight.w600,
        color: color,
      ),
    ),
  );
}

class _LineRow extends StatelessWidget {
  const _LineRow({required this.line});

  final BillingLine line;

  @override
  Widget build(BuildContext context) {
    return Padding(
      key: ValueKey('cloud-bill-line-${line.kind}-${line.label}'),
      padding: const EdgeInsets.symmetric(vertical: 6),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(line.label, style: cineRowTitleStyle()),
                if (line.detail != null)
                  Text(line.detail!, style: cineMutedStyle()),
              ],
            ),
          ),
          const SizedBox(width: 12),
          Text(
            formatCents(line.amountCents, line.currency),
            style: GoogleFonts.firaCode(fontSize: 14, color: cineText),
          ),
        ],
      ),
    );
  }
}
