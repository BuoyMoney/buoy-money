import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import '../l10n/generated/app_localizations.dart';
import '../models/receipt_models.dart';
import '../models/transaction_models.dart';
import '../services/receipts_service.dart';
import '../utils/formatters.dart';
import 'app_toast.dart';
import 'receipt_scan_sheet.dart';

const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _cardBg = Color(0x0DFFFFFF);
const _cardBorder = Color(0x14FFFFFF);

/// Stores whose charges usually cover several categories, where scanning the receipt helps most.
/// Mirrors the merchant patterns in the backend's RetailerDirectory.
const _receiptStores = [
  'walmart', 'wal-mart', 'wal mart', 'wm supercenter', 'wm.com', 'amazon', 'amzn', 'sams club',
  "sam's club", 'samsclub', 'sams scan', 'target', 'costco', 'instacart',
];

/// Whether a charge's merchant is one receipt scanning knows by name.
bool isReceiptStore(Transaction tx) {
  final text = '${tx.merchantName ?? ''} ${tx.description}'.toLowerCase();
  return _receiptStores.any(text.contains);
}

/// On a transaction's detail sheet: the split a scanned receipt offered for this charge (Apply /
/// Dismiss), a note once it was applied, or for a store charge with no receipt yet, a Scan receipt
/// button that matches the receipt to this charge.
class ReceiptSplitCard extends StatefulWidget {
  const ReceiptSplitCard({super.key, required this.transaction, required this.onApplied, this.service});

  final Transaction transaction;
  final Future<void> Function() onApplied;

  /// Injected in tests; the app uses the shared instance.
  final ReceiptsService? service;

  @override
  State<ReceiptSplitCard> createState() => _ReceiptSplitCardState();
}

class _ReceiptSplitCardState extends State<ReceiptSplitCard> {
  ReceiptScan? _scan;
  bool _loaded = false;
  bool _busy = false;

  ReceiptsService get _service => widget.service ?? receiptsService;

  @override
  void initState() {
    super.initState();
    if (isReceiptStore(widget.transaction)) _load();
  }

  Future<void> _load() async {
    try {
      final scan = await _service.forTransaction(widget.transaction.id);
      if (!mounted) return;
      setState(() {
        _scan = scan;
        _loaded = true;
      });
    } catch (_) {
      // Optional extra on the sheet: if it can't load, it just doesn't show.
    }
  }

  Future<void> _scanReceipt() async {
    final applied = await pickAndScanReceipt(context, transactionId: widget.transaction.id, service: widget.service);
    if (!mounted) return;
    if (applied) {
      await widget.onApplied();
    }
    await _load();
  }

  Future<void> _apply() async {
    final l10n = AppLocalizations.of(context);
    setState(() => _busy = true);
    try {
      final updated = await _service.apply(_scan!.id);
      if (!mounted) return;
      setState(() {
        _scan = updated;
        _busy = false;
      });
      if (updated.status == ReceiptStatus.applied) {
        showAppToast(context, l10n.rcptApplied, type: AppToastType.success);
        await widget.onApplied();
      } else {
        showAppToast(context, updated.message ?? l10n.rcptActionFailed, type: AppToastType.error);
      }
    } catch (_) {
      if (!mounted) return;
      setState(() => _busy = false);
      showAppToast(context, l10n.rcptActionFailed, type: AppToastType.error);
    }
  }

  Future<void> _dismiss() async {
    setState(() => _busy = true);
    try {
      await _service.dismiss(_scan!.id);
      if (mounted) setState(() => _scan = null);
    } catch (_) {
      if (mounted) showAppToast(context, AppLocalizations.of(context).rcptActionFailed, type: AppToastType.error);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    if (!_loaded) return const SizedBox.shrink();
    final l10n = AppLocalizations.of(context);
    final scan = _scan;

    if (scan != null && scan.status == ReceiptStatus.applied) {
      return Padding(
        padding: const EdgeInsets.only(top: 12),
        child: Row(children: [
          const Icon(Icons.receipt_long_rounded, size: 14, color: _textMuted),
          const SizedBox(width: 6),
          Expanded(
            child: Text(l10n.rcptSplitFromReceipt(scan.retailer ?? ''),
                style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
          ),
        ]),
      );
    }

    if (scan != null && scan.status == ReceiptStatus.ready) {
      final charge = scan.charges.where((c) => c.transactionId == widget.transaction.id).firstOrNull ??
          scan.charges.firstOrNull;
      return _card(
        icon: Icons.receipt_long_rounded,
        title: l10n.rcptCardTitle(scan.retailer ?? ''),
        children: [
          for (final s in charge?.splits ?? const <ReceiptSplitPart>[])
            Padding(
              padding: const EdgeInsets.only(bottom: 6),
              child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Expanded(
                  child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Text(s.categoryName ?? l10n.rcptCategoryFallback,
                        style: GoogleFonts.sora(fontSize: 13, color: _textGhost)),
                    if (s.items.isNotEmpty)
                      Text(s.items.join(', '),
                          maxLines: 2,
                          overflow: TextOverflow.ellipsis,
                          style: GoogleFonts.sora(fontSize: 11, color: _textMuted)),
                  ]),
                ),
                const SizedBox(width: 8),
                Text(formatCurrency(s.amount), style: GoogleFonts.firaCode(fontSize: 13, color: _textMuted)),
              ]),
            ),
          if (scan.charges.length > 1)
            Padding(
              padding: const EdgeInsets.only(bottom: 6),
              child: Text(l10n.rcptMultipleCharges(scan.charges.length),
                  style: GoogleFonts.sora(fontSize: 11, color: _textMuted)),
            ),
          const SizedBox(height: 6),
          Row(children: [
            FilledButton(
              onPressed: _busy ? null : _apply,
              style: FilledButton.styleFrom(backgroundColor: _accent),
              child: Text(l10n.rcptApply),
            ),
            const SizedBox(width: 8),
            TextButton(
              onPressed: _busy ? null : _dismiss,
              child: Text(l10n.rcptDismiss, style: const TextStyle(color: _textMuted)),
            ),
          ]),
        ],
      );
    }

    if (scan == null && !widget.transaction.isSplit) {
      return _card(
        icon: Icons.document_scanner_outlined,
        title: l10n.rcptCardHint,
        children: [
          Align(
            alignment: Alignment.centerLeft,
            child: TextButton.icon(
              key: const Key('rcpt-card-scan'),
              onPressed: _scanReceipt,
              icon: const Icon(Icons.photo_camera_outlined, size: 18),
              label: Text(l10n.rcptScanReceipt),
            ),
          ),
        ],
      );
    }
    return const SizedBox.shrink();
  }

  Widget _card({required IconData icon, required String title, required List<Widget> children}) {
    return Padding(
      padding: const EdgeInsets.only(top: 12),
      child: Container(
        width: double.infinity,
        padding: const EdgeInsets.all(16),
        decoration: BoxDecoration(
          color: _cardBg,
          borderRadius: BorderRadius.circular(16),
          border: Border.all(color: _cardBorder),
        ),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Row(children: [
            Icon(icon, size: 14, color: _accent),
            const SizedBox(width: 6),
            Expanded(
              child: Text(title,
                  style: GoogleFonts.sora(fontSize: 12, color: _textMuted, fontWeight: FontWeight.w500)),
            ),
          ]),
          const SizedBox(height: 10),
          ...children,
        ]),
      ),
    );
  }
}
