import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';

import '../l10n/generated/app_localizations.dart';
import '../services/export_service.dart';
import '../utils/api_error.dart';
import '../utils/file_download.dart';
import 'app_toast.dart';
import 'step_up_mfa_dialog.dart';

// Same palette as the settings page it opens from.
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _cardBorder = Color(0x14FFFFFF);
const _expense = Color(0xFFF87171);

typedef ExportDownloader = Future<ExportFile> Function(ExportFormat format);
typedef ExportSaver =
    Future<void> Function(
      Uint8List bytes, {
      required String filename,
      required String mimeType,
      Rect? sharePositionOrigin,
    });

/// Runs the step-up MFA prompt; true when the user verified.
typedef ExportStepUp = Future<bool> Function(BuildContext context);

/// Bottom sheet behind Settings > Privacy & Data > Download my data.
/// The user picks JSON or a CSV zip; the file is fetched and then saved
/// (browser download on web, share sheet on mobile).
class DataExportSheet extends StatefulWidget {
  /// Overridable for tests; defaults to the live service and platform saver.
  final ExportDownloader? downloader;
  final ExportSaver? saver;
  final ExportStepUp? stepUp;

  const DataExportSheet({super.key, this.downloader, this.saver, this.stepUp});

  @override
  State<DataExportSheet> createState() => _DataExportSheetState();
}

class _DataExportSheetState extends State<DataExportSheet> {
  ExportFormat _format = ExportFormat.json;
  bool _busy = false;
  String? _error;
  final _buttonKey = GlobalKey();

  /// Where the iPad share popover anchors: the Download button.
  Rect? _shareOrigin() {
    final box = _buttonKey.currentContext?.findRenderObject();
    if (box is! RenderBox || !box.hasSize) return null;
    return box.localToGlobal(Offset.zero) & box.size;
  }

  static bool _isMfaRequired(Object e) {
    if (e is! DioException || e.response?.statusCode != 403) return false;
    final data = e.response?.data;
    return data is Map && data['error'] == 'MfaRequired';
  }

  Future<ExportFile> _fetch(BuildContext context) async {
    final download = widget.downloader ?? exportService.downloadExport;
    try {
      return await download(_format);
    } catch (e) {
      if (!_isMfaRequired(e)) rethrow;
      // Accounts with MFA must step up first, the same as for other sensitive actions.
      final verify =
          widget.stepUp ??
          (c) async =>
              await showStepUpMfaDialog(
                c,
                operation: 'ViewSensitiveFinancialData',
              ) !=
              null;
      if (!context.mounted || !await verify(context)) rethrow;
      return download(_format);
    }
  }

  Future<void> _download() async {
    if (_busy) return;
    final l10n = AppLocalizations.of(context);
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final origin = _shareOrigin();
      final file = await _fetch(context);
      await (widget.saver ?? saveBytesAsFile)(
        file.bytes,
        filename: file.filename,
        mimeType: file.mimeType,
        sharePositionOrigin: origin,
      );
      if (!mounted) return;
      showAppToast(context, l10n.exportSuccess, type: AppToastType.success);
      Navigator.pop(context);
    } catch (e) {
      if (!mounted) return;
      final rateLimited = e is DioException && e.response?.statusCode == 429;
      // The server's concurrency gate (one export at a time) says so with a code; the
      // 10-minute window limit does not.
      final busy = rateLimited && _isExportBusy(e);
      setState(() {
        _busy = false;
        _error = busy
            ? l10n.exportBusy
            : rateLimited
            ? l10n.exportRateLimited
            : apiErrorMessage(e, fallback: l10n.exportFailed);
      });
    }
  }

  static bool _isExportBusy(DioException e) {
    final data = e.response?.data;
    return data is Map && data['code'] == 'export_in_progress';
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return SafeArea(
      child: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(24, 12, 24, 24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Center(
              child: Container(
                width: 40,
                height: 4,
                decoration: BoxDecoration(
                  color: _textMuted.withValues(alpha: 0.3),
                  borderRadius: BorderRadius.circular(2),
                ),
              ),
            ),
            const SizedBox(height: 16),
            Text(
              l10n.exportDownloadTitle,
              style: GoogleFonts.sora(
                fontSize: 18,
                fontWeight: FontWeight.w700,
                color: _textGhost,
              ),
            ),
            const SizedBox(height: 8),
            Text(
              l10n.exportSheetDescription,
              style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
            ),
            const SizedBox(height: 16),
            _FormatOption(
              key: const ValueKey('export-format-json'),
              title: l10n.exportFormatJson,
              subtitle: l10n.exportFormatJsonDesc,
              selected: _format == ExportFormat.json,
              onTap: _busy
                  ? null
                  : () => setState(() => _format = ExportFormat.json),
            ),
            const SizedBox(height: 10),
            _FormatOption(
              key: const ValueKey('export-format-csv'),
              title: l10n.exportFormatCsv,
              subtitle: l10n.exportFormatCsvDesc,
              selected: _format == ExportFormat.csv,
              onTap: _busy
                  ? null
                  : () => setState(() => _format = ExportFormat.csv),
            ),
            if (_error != null) ...[
              const SizedBox(height: 14),
              Text(
                _error!,
                key: const ValueKey('export-error'),
                style: GoogleFonts.sora(fontSize: 12, color: _expense),
              ),
            ],
            const SizedBox(height: 18),
            SizedBox(
              key: _buttonKey,
              width: double.infinity,
              child: FilledButton(
                key: const ValueKey('export-download-button'),
                style: FilledButton.styleFrom(
                  backgroundColor: _accent,
                  padding: const EdgeInsets.symmetric(vertical: 14),
                ),
                onPressed: _busy ? null : _download,
                child: _busy
                    ? Row(
                        mainAxisAlignment: MainAxisAlignment.center,
                        children: [
                          const SizedBox(
                            width: 16,
                            height: 16,
                            child: CircularProgressIndicator(
                              strokeWidth: 2,
                              color: Colors.white,
                            ),
                          ),
                          const SizedBox(width: 10),
                          Text(
                            l10n.exportPreparing,
                            style: GoogleFonts.sora(
                              fontWeight: FontWeight.w700,
                              color: Colors.white,
                            ),
                          ),
                        ],
                      )
                    : Text(
                        l10n.exportDownloadButton,
                        style: GoogleFonts.sora(
                          fontWeight: FontWeight.w700,
                          color: Colors.white,
                        ),
                      ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _FormatOption extends StatelessWidget {
  final String title;
  final String subtitle;
  final bool selected;
  final VoidCallback? onTap;

  const _FormatOption({
    super.key,
    required this.title,
    required this.subtitle,
    required this.selected,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return Semantics(
      button: true,
      selected: selected,
      label: title,
      // InkWell is focusable and activates on Enter/Space, so keyboard users on web can pick a format.
      child: Material(
        type: MaterialType.transparency,
        child: Ink(
          decoration: BoxDecoration(
            color: selected
                ? _accent.withValues(alpha: 0.12)
                : Colors.transparent,
            borderRadius: BorderRadius.circular(12),
            border: Border.all(color: selected ? _accent : _cardBorder),
          ),
          child: InkWell(
            onTap: onTap,
            borderRadius: BorderRadius.circular(12),
            child: Padding(
              padding: const EdgeInsets.all(14),
              child: Row(
                children: [
                  Icon(
                    selected
                        ? Icons.radio_button_checked_rounded
                        : Icons.radio_button_unchecked_rounded,
                    size: 20,
                    color: selected ? _accent : _textMuted,
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          title,
                          style: GoogleFonts.sora(
                            fontSize: 14,
                            fontWeight: FontWeight.w600,
                            color: _textGhost,
                          ),
                        ),
                        const SizedBox(height: 2),
                        Text(
                          subtitle,
                          style: GoogleFonts.sora(
                            fontSize: 12,
                            color: _textMuted,
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}
