import '../../l10n/generated/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import '../../services/gamification_service.dart';

// VaporClinic palette (shared with the achievements page).
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _income = Color(0xFF34D399);
const _pending = Color(0xFFFBBF24);
const _cardBorder = Color(0x14FFFFFF);

const _months = [
  'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
  'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'
];

String formatAchievementDate(DateTime d) =>
    '${_months[d.month - 1]} ${d.day}, ${d.year}';

// Rarity-tier accent colors.
const _tierColors = <String, Color>{
  'Bronze': Color(0xFFCD7F32),
  'Silver': Color(0xFFC0C0C0),
  'Gold': Color(0xFFFFD700),
  'Platinum': Color(0xFF9B8CFF),
};

Color _tierColor(String? tier) => _tierColors[tier ?? ''] ?? _accent;

/// The pre-rendered badge art lives at `assets/achievements/<key>.png` (one per
/// achievement key; see scripts/badges/). Rendered full-color when earned.
String _badgeAsset(String key) => 'assets/achievements/$key.png';

/// Full-color badge art for an earned/revealed achievement. Falls back to the
/// server-supplied emoji (then a trophy) if an asset is ever missing.
Widget _badgeImage(AchievementCatalogItem item, double size) {
  return Image.asset(
    _badgeAsset(item.key),
    width: size,
    height: size,
    filterQuality: FilterQuality.medium,
    errorBuilder: (_, __, ___) => Text(
      item.icon ?? '🏆',
      style: TextStyle(fontSize: size * 0.8),
    ),
  );
}

/// A uniform "mystery medallion" for a locked good achievement. Every locked
/// tile looks identical so the badge's identity never leaks through its shape
/// (the server also masks its title/icon). Reveals the real art once earned.
Widget _lockedMedallion(double size) {
  return Container(
    width: size,
    height: size,
    decoration: BoxDecoration(
      shape: BoxShape.circle,
      gradient: const RadialGradient(
        colors: [Color(0x22FFFFFF), Color(0x0AFFFFFF)],
      ),
      border: Border.all(color: _cardBorder, width: 2),
    ),
    child: const Icon(Icons.lock_outline, size: 22, color: _textMuted),
  );
}

/// A "good" badge tile — full color when earned, a locked silhouette otherwise.
/// Locked tiles never reveal the title/icon (the server masks them too). Earned tiles
/// are accented by rarity tier and show how rare the badge is.
class GoodAchievementBadge extends StatelessWidget {
  final AchievementCatalogItem item;
  const GoodAchievementBadge({super.key, required this.item});

  @override
  Widget build(BuildContext context) {
    final earned = item.isEarned;
    final tier = _tierColor(item.tier);
    final rarity = item.percentEarned > 0
        ? '${item.percentEarned % 1 == 0 ? item.percentEarned.toInt() : item.percentEarned}% have this'
        : null;
    final tooltip = earned
        ? '${item.description ?? ''}${item.tier != null ? '\n${item.tier} tier${rarity != null ? ' · $rarity' : ''}' : ''}'
        : 'Locked — keep going to reveal this achievement.';

    return Tooltip(
      message: tooltip,
      child: Container(
        width: 150,
        height: 150,
        padding: const EdgeInsets.all(14),
        decoration: BoxDecoration(
          color: earned ? tier.withValues(alpha: 0.10) : const Color(0x08FFFFFF),
          borderRadius: BorderRadius.circular(16),
          border: Border.all(
            color: earned ? tier.withValues(alpha: 0.45) : _cardBorder,
          ),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                if (earned)
                  _badgeImage(item, 46)
                else
                  _lockedMedallion(44),
                const Spacer(),
                if (earned && item.tier != null)
                  Container(
                    padding:
                        const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                    decoration: BoxDecoration(
                      color: tier.withValues(alpha: 0.18),
                      borderRadius: BorderRadius.circular(999),
                    ),
                    child: Text(item.tier!.toUpperCase(),
                        style: GoogleFonts.sora(
                            fontSize: 8,
                            fontWeight: FontWeight.w700,
                            letterSpacing: 0.5,
                            color: tier)),
                  ),
              ],
            ),
            const Spacer(),
            Text(
              earned ? (item.title ?? '') : '???',
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
              style: GoogleFonts.sora(
                fontSize: 13,
                fontWeight: FontWeight.w700,
                color: earned ? _textGhost : _textMuted,
              ),
            ),
            const SizedBox(height: 4),
            Text(
              earned
                  ? (rarity ??
                      (item.earnedAt != null
                          ? formatAchievementDate(item.earnedAt!)
                          : '${item.points} pts'))
                  : '${item.points} pts',
              style: GoogleFonts.sora(
                fontSize: 10,
                color: earned ? _income : _textMuted,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// A cautionary ("oops") badge row — always legible, with the explanation inline
/// and in a tooltip. Highlighted amber when earned, muted-but-readable otherwise.
class OopsAchievementBadge extends StatelessWidget {
  final AchievementCatalogItem item;
  const OopsAchievementBadge({super.key, required this.item});

  @override
  Widget build(BuildContext context) {
    final earned = item.isEarned;
    return Tooltip(
      message: item.description ?? '',
      child: Container(
        padding: const EdgeInsets.all(14),
        decoration: BoxDecoration(
          color: _pending.withValues(alpha: earned ? 0.10 : 0.04),
          borderRadius: BorderRadius.circular(14),
          border: Border.all(
            color: _pending.withValues(alpha: earned ? 0.45 : 0.18),
          ),
        ),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // Oops badges are always revealed; slightly dimmed when not yet earned.
            Opacity(opacity: earned ? 1.0 : 0.55, child: _badgeImage(item, 44)),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Flexible(
                        child: Text(
                          item.title ?? '',
                          style: GoogleFonts.sora(
                              fontSize: 14,
                              fontWeight: FontWeight.w700,
                              color: _textGhost),
                        ),
                      ),
                      const SizedBox(width: 8),
                      if (earned)
                        _tag(item.timesEarned > 1 ? 'EARNED ×${item.timesEarned}' : 'EARNED', _pending)
                      else
                        _tag('NOT EARNED', _textMuted),
                    ],
                  ),
                  const SizedBox(height: 4),
                  Text(
                    item.description ?? '',
                    style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
                  ),
                  if (earned && item.earnedAt != null) ...[
                    const SizedBox(height: 4),
                    Text(AppLocalizations.of(context).uiEarnedOn(formatAchievementDate(item.earnedAt!)),
                        style: GoogleFonts.sora(fontSize: 10, color: _pending)),
                  ],
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _tag(String text, Color color) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
        decoration: BoxDecoration(
          color: color.withValues(alpha: 0.15),
          borderRadius: BorderRadius.circular(999),
        ),
        child: Text(text,
            style: GoogleFonts.sora(
                fontSize: 9, fontWeight: FontWeight.w700, color: color)),
      );
}
