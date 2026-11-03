import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:image_picker/image_picker.dart';
import 'package:provider/provider.dart';
import '../l10n/generated/app_localizations.dart';
import '../models/receipt_models.dart';
import '../providers/auth_provider.dart';
import '../services/receipts_service.dart';
import '../utils/formatters.dart';
import 'ai_consent_sheet.dart';
import 'app_toast.dart';

/// One receipt is one share: a long order's screenshots go together (the server reads them as
/// one receipt), and each order is shared separately. Matches ReceiptFiles.MaxFiles.
const maxReceiptFiles = 8;

/// The server's limits (ReceiptFiles.MaxFileBytes and MaxTotalBytes), checked before any file is read.
const maxReceiptFileBytes = 8 * 1024 * 1024;
const maxReceiptTotalBytes = 16 * 1024 * 1024;

/// Reads picked or shared files, checking their sizes first so a huge photo or PDF is never loaded
/// into memory. Null when they're too big to send.
Future<List<ReceiptUpload>?> readReceiptFiles(Iterable<XFile> picked) async {
  final chosen = picked.take(maxReceiptFiles).toList();
  var total = 0;
  for (final x in chosen) {
    final size = await x.length();
    if (size > maxReceiptFileBytes) return null;
    total += size;
  }
  if (total > maxReceiptTotalBytes) return null;
  return [for (final x in chosen) ReceiptUpload(await x.readAsBytes(), x.name)];
}

/// Lets the user take a photo of a receipt or pick screenshots of an order page, then opens
/// [showReceiptScanSheet]. Returns true when a split was applied.
Future<bool> pickAndScanReceipt(BuildContext context, {int? transactionId, ReceiptsService? service}) async {
  final l10n = AppLocalizations.of(context);
  final source = await showModalBottomSheet<String>(
    context: context,
    builder: (context) => SafeArea(
      child: Column(mainAxisSize: MainAxisSize.min, children: [
        ListTile(
          leading: const Icon(Icons.photo_camera_outlined),
          title: Text(l10n.rcptTakePhoto),
          subtitle: Text(l10n.rcptTakePhotoHint),
          onTap: () => Navigator.pop(context, 'camera'),
        ),
        ListTile(
          leading: const Icon(Icons.photo_library_outlined),
          title: Text(l10n.rcptChooseScreenshots),
          subtitle: Text(l10n.rcptChooseScreenshotsHint),
          onTap: () => Navigator.pop(context, 'gallery'),
        ),
      ]),
    ),
  );
  if (source == null) return false;

  final picker = ImagePicker();
  List<XFile> picked;
  try {
    if (source == 'camera') {
      final photo = await picker.pickImage(source: ImageSource.camera, imageQuality: 85, maxWidth: 2400);
      picked = photo == null ? [] : [photo];
    } else {
      picked = await picker.pickMultiImage(imageQuality: 85, maxWidth: 2400, limit: maxReceiptFiles);
    }
  } catch (_) {
    picked = [];
  }
  if (picked.isEmpty || !context.mounted) return false;

  final files = await readReceiptFiles(picked);
  if (!context.mounted) return false;
  if (files == null) {
    showAppToast(context, ReceiptsService.tooLargeMessage, type: AppToastType.error);
    return false;
  }
  return await showReceiptScanSheet(context, files, transactionId: transactionId, service: service) ?? false;
}

/// The receipt sheet: shows exactly what will be sent, sends it on the user's tap, then shows
/// the split BuoyAI offers with Apply. Used by the share sheet (Share → BuoyAI), the Receipts page
/// and a transaction's "Scan receipt". Returns true when a split was applied.
Future<bool?> showReceiptScanSheet(
  BuildContext context,
  List<ReceiptUpload> files, {
  int? transactionId,
  ReceiptsService? service,
}) {
  return showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    builder: (_) => ReceiptScanSheet(files: files, transactionId: transactionId, service: service),
  );
}

class ReceiptScanSheet extends StatefulWidget {
  const ReceiptScanSheet({super.key, required this.files, this.transactionId, this.service});

  final List<ReceiptUpload> files;
  final int? transactionId;

  /// Injected in tests; the app uses the shared instance.
  final ReceiptsService? service;

  @override
  State<ReceiptScanSheet> createState() => _ReceiptScanSheetState();
}

enum _Stage { preview, sending, result, error }

class _ReceiptScanSheetState extends State<ReceiptScanSheet> {
  _Stage _stage = _Stage.preview;
  ReceiptScan? _scan;
  String? _error;
  ReceiptBlocker _blocker = ReceiptBlocker.none;
  bool _applying = false;

  ReceiptsService get _service => widget.service ?? receiptsService;

  Future<void> _send() async {
    setState(() => _stage = _Stage.sending);
    try {
      final scan = await _service.scan(widget.files, transactionId: widget.transactionId);
      if (!mounted) return;
      setState(() {
        _scan = scan;
        _stage = _Stage.result;
      });
    } on ReceiptScanException catch (e) {
      if (!mounted) return;
      setState(() {
        _error = e.message;
        _blocker = e.blocker;
        _stage = _Stage.error;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _error = null;
        _stage = _Stage.error;
      });
    }
  }

  Future<void> _apply() async {
    final l10n = AppLocalizations.of(context);
    setState(() => _applying = true);
    try {
      final result = await _service.apply(_scan!.id);
      if (!mounted) return;
      if (result.status == ReceiptStatus.applied) {
        showAppToast(context, l10n.rcptApplied, type: AppToastType.success);
        Navigator.of(context).pop(true);
        return;
      }
      showAppToast(context, result.message ?? l10n.rcptActionFailed, type: AppToastType.error);
    } catch (_) {
      if (mounted) showAppToast(context, l10n.rcptActionFailed, type: AppToastType.error);
    }
    if (mounted) setState(() => _applying = false);
  }

  Future<void> _turnOnAi() async {
    // Dismissed or declined: keep the preview as it is and don't send.
    if (!await showAiConsentSheet(context) || !mounted) return;
    await context.read<AuthProvider?>()?.refreshUser();
    if (mounted) await _send();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return SingleChildScrollView(
      // A receipt can offer many splits; the sheet scrolls so Apply never falls off-screen.
      padding: EdgeInsets.fromLTRB(20, 16, 20, 20 + MediaQuery.of(context).viewInsets.bottom),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(l10n.rcptSheetTitle, style: Theme.of(context).textTheme.titleLarge),
          const SizedBox(height: 12),
          _thumbnails(),
          const SizedBox(height: 16),
          ...switch (_stage) {
            _Stage.preview => _preview(l10n),
            _Stage.sending => [
                Row(children: [
                  const SizedBox(width: 20, height: 20, child: CircularProgressIndicator(strokeWidth: 2)),
                  const SizedBox(width: 12),
                  Expanded(child: Text(l10n.rcptReading)),
                ]),
              ],
            _Stage.result => _result(l10n),
            _Stage.error => _errorView(l10n),
          },
        ],
      ),
    );
  }

  Widget _thumbnails() {
    return SizedBox(
      height: 120,
      child: ListView.separated(
        scrollDirection: Axis.horizontal,
        itemCount: widget.files.length,
        separatorBuilder: (_, __) => const SizedBox(width: 8),
        itemBuilder: (context, i) {
          final f = widget.files[i];
          final fallback = Container(
            width: 90,
            color: Theme.of(context).colorScheme.surfaceContainerHighest,
            alignment: Alignment.center,
            child: Icon(f.isPdf ? Icons.picture_as_pdf_outlined : Icons.image_outlined, size: 36),
          );
          return ClipRRect(
            borderRadius: BorderRadius.circular(8),
            child: f.isPdf
                ? fallback
                : Image.memory(
                    Uint8List.fromList(f.bytes),
                    key: Key('rcpt-thumb-$i'),
                    fit: BoxFit.cover,
                    width: 90,
                    errorBuilder: (_, __, ___) => fallback,
                  ),
          );
        },
      ),
    );
  }

  List<Widget> _preview(AppLocalizations l10n) => [
        if (widget.files.length > 1) ...[
          Text(l10n.rcptSheetMany(widget.files.length), key: const Key('rcpt-many')),
          const SizedBox(height: 8),
        ],
        Text(l10n.rcptSheetNotice, style: _muted()),
        const SizedBox(height: 16),
        Row(children: [
          FilledButton(key: const Key('rcpt-send'), onPressed: _send, child: Text(l10n.rcptSend)),
          const SizedBox(width: 8),
          TextButton(onPressed: () => Navigator.of(context).pop(false), child: Text(l10n.rcptCancel)),
        ]),
      ];

  List<Widget> _result(AppLocalizations l10n) {
    final scan = _scan!;
    if (scan.status == ReceiptStatus.ready) {
      return [
        Text(
          [scan.retailer ?? l10n.rcptUnknownStore, formatCurrency(scan.chargedTotal)].join('  '),
          style: const TextStyle(fontWeight: FontWeight.w600),
        ),
        const SizedBox(height: 8),
        for (final s in scan.charges.expand((c) => c.splits))
          Padding(
            padding: const EdgeInsets.only(bottom: 6),
            child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Expanded(
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Text(s.categoryName ?? l10n.rcptCategoryFallback),
                  if (s.items.isNotEmpty)
                    Text(s.items.join(', '), maxLines: 2, overflow: TextOverflow.ellipsis, style: _muted()),
                ]),
              ),
              Text(formatCurrency(s.amount)),
            ]),
          ),
        if (scan.charges.length > 1) Text(l10n.rcptMultipleCharges(scan.charges.length), style: _muted()),
        const SizedBox(height: 12),
        Row(children: [
          FilledButton(
            key: const Key('rcpt-apply'),
            onPressed: _applying ? null : _apply,
            child: Text(l10n.rcptApply),
          ),
          const SizedBox(width: 8),
          TextButton(onPressed: () => Navigator.of(context).pop(false), child: Text(l10n.rcptNotNow)),
        ]),
      ];
    }
    return [
      Text(scan.message ?? l10n.rcptActionFailed),
      const SizedBox(height: 12),
      FilledButton(onPressed: () => Navigator.of(context).pop(false), child: Text(l10n.rcptOk)),
    ];
  }

  List<Widget> _errorView(AppLocalizations l10n) => [
        Text(_error ?? l10n.rcptReadFailed),
        const SizedBox(height: 12),
        Wrap(spacing: 8, children: [
          if (_blocker == ReceiptBlocker.premiumRequired)
            FilledButton(
              onPressed: () {
                final router = GoRouter.of(context);
                Navigator.of(context).pop(false);
                router.push('/billing');
              },
              child: Text(l10n.rcptSeePremium),
            )
          else if (_blocker == ReceiptBlocker.aiConsentRequired)
            FilledButton(onPressed: _turnOnAi, child: Text(l10n.rcptTurnOnAi))
          else if (_blocker == ReceiptBlocker.none)
            FilledButton(onPressed: _send, child: Text(l10n.rcptRetry)),
          TextButton(onPressed: () => Navigator.of(context).pop(false), child: Text(l10n.rcptCancel)),
        ]),
      ];

  TextStyle _muted() =>
      TextStyle(fontSize: 13, color: Theme.of(context).colorScheme.onSurface.withValues(alpha: 0.7));
}
