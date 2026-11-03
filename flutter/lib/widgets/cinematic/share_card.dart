import '../../config/app_brand.dart';
import '../../l10n/generated/app_localizations.dart';
import 'dart:typed_data';
import 'dart:ui' as ui;
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:share_plus/share_plus.dart';

const _brandBlue = Color(0xFF094AAA);
const _brandGreen = Color(0xFF1EB442);
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF9AA3B2);

const _tierColors = <String, Color>{
  'Bronze': Color(0xFFCD7F32),
  'Silver': Color(0xFFC0C0C0),
  'Gold': Color(0xFFFFD700),
  'Platinum': Color(0xFF9B8CFF),
};

/// Data for a shareable card. Privacy-safe by construction — there is deliberately NO
/// field for balances or dollar amounts. Only relative/abstract progress is shareable.
class ShareCardData {
  final String kicker; // e.g. "ACHIEVEMENT UNLOCKED", "STREAK", "LEVEL"
  final String icon; // emoji (fallback when [assetPath] is null)
  final String title;
  final String? subtitle; // e.g. "Gold tier · 7% have this", "42 days"
  final String? tier;

  /// Optional pre-rendered badge art (`assets/achievements/<key>.png`). When set,
  /// the card shows the real badge instead of the emoji.
  final String? assetPath;

  const ShareCardData({
    required this.kicker,
    required this.icon,
    required this.title,
    this.subtitle,
    this.tier,
    this.assetPath,
  });
}

/// A fixed-size, branded card designed to be rasterized and shared. Never render dollar
/// amounts here (GLBA/privacy — see docs/gamification/gamification-redesign.md §4.4).
class ShareCard extends StatelessWidget {
  final ShareCardData data;
  const ShareCard({super.key, required this.data});

  @override
  Widget build(BuildContext context) {
    final tier = _tierColors[data.tier ?? ''] ?? _accent;
    return Container(
      width: 380,
      height: 480,
      padding: const EdgeInsets.all(28),
      decoration: const BoxDecoration(
        gradient: LinearGradient(
          begin: Alignment.topLeft,
          end: Alignment.bottomRight,
          colors: [Color(0xFF0A1230), Color(0xFF04040A)],
        ),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // Brand wordmark
          Row(
            children: [
              Container(
                width: 26,
                height: 26,
                decoration: const BoxDecoration(
                  shape: BoxShape.circle,
                  gradient: LinearGradient(colors: [_brandBlue, _brandGreen]),
                ),
              ),
              const SizedBox(width: 8),
              Text(AppBrand.name,
                  style: GoogleFonts.sora(
                      fontSize: 18,
                      fontWeight: FontWeight.w800,
                      color: _textGhost)),
            ],
          ),
          const Spacer(),
          Text(data.kicker,
              style: GoogleFonts.sora(
                  fontSize: 12,
                  fontWeight: FontWeight.w700,
                  letterSpacing: 2,
                  color: tier)),
          const SizedBox(height: 18),
          // Real badge art (with its own coin rim) when provided; otherwise the
          // tier-ringed emoji disc used by the archetype/progress cards.
          if (data.assetPath != null)
            Image.asset(
              data.assetPath!,
              width: 112,
              height: 112,
              filterQuality: FilterQuality.medium,
              errorBuilder: (_, __, ___) =>
                  Text(data.icon, style: const TextStyle(fontSize: 46)),
            )
          else
            Container(
              width: 96,
              height: 96,
              alignment: Alignment.center,
              decoration: BoxDecoration(
                shape: BoxShape.circle,
                color: tier.withValues(alpha: 0.14),
                border:
                    Border.all(color: tier.withValues(alpha: 0.6), width: 2),
              ),
              child: Text(data.icon, style: const TextStyle(fontSize: 46)),
            ),
          const SizedBox(height: 20),
          Text(data.title,
              style: GoogleFonts.sora(
                  fontSize: 28,
                  fontWeight: FontWeight.w800,
                  height: 1.1,
                  color: _textGhost)),
          if (data.subtitle != null) ...[
            const SizedBox(height: 8),
            Text(data.subtitle!,
                style: GoogleFonts.sora(fontSize: 14, color: tier)),
          ],
          const Spacer(),
          Row(
            children: [
              Expanded(
                child: Text(AppLocalizations.of(context).uiShareTagline,
                    style:
                        GoogleFonts.sora(fontSize: 12, color: _textMuted)),
              ),
              Text('buoy-ai.com',
                  style: GoogleFonts.sora(
                      fontSize: 12,
                      fontWeight: FontWeight.w600,
                      color: _brandGreen)),
            ],
          ),
        ],
      ),
    );
  }
}

/// Rasterize a keyed RepaintBoundary to PNG bytes (3x for crisp share output).
Future<Uint8List?> captureCardPng(GlobalKey boundaryKey) async {
  final ctx = boundaryKey.currentContext;
  if (ctx == null) return null;
  final boundary = ctx.findRenderObject() as RenderRepaintBoundary?;
  if (boundary == null) return null;
  final image = await boundary.toImage(pixelRatio: 3.0);
  final byteData = await image.toByteData(format: ui.ImageByteFormat.png);
  return byteData?.buffer.asUint8List();
}

/// Present a share preview for [data]: the branded card + a Share button that rasterizes
/// it and hands it to the OS-native share sheet (Web Share API on web) with [caption].
Future<void> showShareCardSheet(
  BuildContext context,
  ShareCardData data, {
  required String caption,
}) async {
  final boundaryKey = GlobalKey();
  await showModalBottomSheet<void>(
    context: context,
    backgroundColor: const Color(0xFF14141F),
    isScrollControlled: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(24)),
    ),
    builder: (sheetContext) => SafeArea(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(20, 16, 20, 24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Container(
              width: 40,
              height: 4,
              decoration: BoxDecoration(
                color: _textMuted.withValues(alpha: 0.4),
                borderRadius: BorderRadius.circular(2),
              ),
            ),
            const SizedBox(height: 16),
            ClipRRect(
              borderRadius: BorderRadius.circular(20),
              child: RepaintBoundary(
                key: boundaryKey,
                child: ShareCard(data: data),
              ),
            ),
            const SizedBox(height: 20),
            SizedBox(
              width: double.infinity,
              child: FilledButton.icon(
                style: FilledButton.styleFrom(
                  backgroundColor: _accent,
                  padding: const EdgeInsets.symmetric(vertical: 14),
                ),
                onPressed: () async {
                  final bytes = await captureCardPng(boundaryKey);
                  if (bytes == null) return;
                  await SharePlus.instance.share(ShareParams(
                    text: caption,
                    files: [
                      XFile.fromData(bytes,
                          mimeType: 'image/png',
                          name: 'buoyai-achievement.png'),
                    ],
                  ));
                },
                icon: const Icon(Icons.ios_share, size: 18),
                label: Text(AppLocalizations.of(context).uiShare,
                    style: GoogleFonts.sora(
                        fontWeight: FontWeight.w700, color: Colors.white)),
              ),
            ),
          ],
        ),
      ),
    ),
  );
}
