import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import '../l10n/generated/app_localizations.dart';

const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _cardBorder = Color(0x14FFFFFF);
const _sheetBg = Color(0xFF0C0C18);
const _accent = Color(0xFF7B61FF);

/// After a real category correction, offer to make it a rule: "Always file
/// {merchant} as {category}?" Resolves true for "Always", false for "Just this
/// one", null if dismissed. The caller decides whether to ask at all (only on a
/// correction, once per merchant) so this stays a plain question.
Future<bool?> showAlwaysCategorizeOffer(
  BuildContext context, {
  required String merchant,
  required String category,
}) {
  return showDialog<bool>(
    context: context,
    builder: (ctx) {
      final l10n = AppLocalizations.of(ctx);
      return AlertDialog(
        backgroundColor: _sheetBg,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(16),
          side: const BorderSide(color: _cardBorder),
        ),
        title: Row(
          children: [
            const Icon(Icons.auto_fix_high, size: 18, color: _accent),
            const SizedBox(width: 8),
            Expanded(
              child: Text(
                l10n.txnAlwaysCategorizeTitle(merchant, category),
                style: GoogleFonts.sora(
                    fontSize: 16, color: _textGhost, fontWeight: FontWeight.w600),
              ),
            ),
          ],
        ),
        content: Text(
          l10n.txnAlwaysCategorizeBody(merchant),
          style: GoogleFonts.sora(fontSize: 13, color: _textMuted, height: 1.4),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(false),
            child: Text(l10n.txnAlwaysCategorizeNo,
                style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
          ),
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(true),
            child: Text(l10n.txnAlwaysCategorizeYes,
                style: GoogleFonts.sora(
                    fontSize: 13, color: _accent, fontWeight: FontWeight.w600)),
          ),
        ],
      );
    },
  );
}
