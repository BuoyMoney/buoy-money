import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';

import '../glass_card.dart';
import 'backgrounds/deep_water_current.dart';
import 'backgrounds/interactive_background_layer.dart';
import 'noise_overlay.dart';

// ── VaporClinic palette (the same values the Settings and Profile pages use) ──
const cineAccent = Color(0xFF7B61FF);
const cineText = Color(0xFFF0EFF4);
const cineMuted = Color(0xFF8A8A9A);
const cineCardBorder = Color(0x14FFFFFF);
const cineCardBg = Color(0x0DFFFFFF);
const cineIncome = Color(0xFF34D399);
const cineExpense = Color(0xFFF87171);
const cineWarning = Color(0xFFFBBF24);
const cineSheetBg = Color(0xFF0C0C18);
const cineBackground = Color(0xFF04040A);

/// Body copy on a glass card.
TextStyle cineBodyStyle({Color? color}) => GoogleFonts.sora(
    fontSize: 13,
    height: 1.5,
    color: color ?? cineText.withValues(alpha: 0.85));

/// Secondary copy on a glass card.
TextStyle cineMutedStyle({double size = 12}) =>
    GoogleFonts.sora(fontSize: size, height: 1.45, color: cineMuted);

/// A row's title on a glass card.
TextStyle cineRowTitleStyle() => GoogleFonts.sora(
    fontSize: 14, fontWeight: FontWeight.w600, color: cineText);

/// A settings sub-page in the app's look: no app bar, a back arrow and an Instrument Serif title over the
/// animated background, then glass cards. Inside the nav shell the shell paints the background, so the page is
/// transparent. A top-level route (outside the shell) passes [backdrop] to paint the same background itself.
class GlassSubPage extends StatelessWidget {
  const GlassSubPage({
    super.key,
    required this.title,
    required this.children,
    this.intro,
    this.fallbackPath = '/settings',
    this.backdrop = false,
    this.animateBackdrop = true,
    this.maxWidth = 720,
    this.showBack = true,
    this.onRefresh,
  });

  final String title;
  final String? intro;
  final List<Widget> children;

  /// Where the back arrow goes when there is nothing to pop (the page was opened by a link).
  final String fallbackPath;

  /// Paint the animated background and grain here (for routes outside the nav shell).
  final bool backdrop;

  /// False in widget tests: the animated background never settles, so a still frame of it is painted instead.
  final bool animateBackdrop;
  final double maxWidth;
  final bool showBack;

  /// Pull to refresh, for pages that load from the server.
  final Future<void> Function()? onRefresh;

  void _back(BuildContext context) {
    // The router first: a page pushed onto the shell's navigator from a top-level route (the Cloud page opened
    // from the link approval) cannot pop to that route through the nearest navigator, and `go` would lose it.
    final router = GoRouter.maybeOf(context);
    if (router != null && router.canPop()) {
      router.pop();
      return;
    }
    final navigator = Navigator.of(context);
    if (navigator.canPop()) {
      navigator.pop();
      return;
    }
    router?.go(fallbackPath);
  }

  @override
  Widget build(BuildContext context) {
    final list = ListView(
      physics: onRefresh != null ? const AlwaysScrollableScrollPhysics() : null,
      padding: const EdgeInsets.fromLTRB(16, 12, 16, 120),
      children: [
        Center(
          child: ConstrainedBox(
            constraints: BoxConstraints(maxWidth: maxWidth),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Row(
                  children: [
                    if (showBack)
                      IconButton(
                        key: const ValueKey('glass-subpage-back'),
                        tooltip:
                            MaterialLocalizations.of(context).backButtonTooltip,
                        onPressed: () => _back(context),
                        icon: const Icon(Icons.arrow_back_rounded,
                            color: cineText),
                      )
                    else
                      const SizedBox(width: 8),
                    const SizedBox(width: 4),
                    Expanded(
                      child: Text(
                        title,
                        style: GoogleFonts.instrumentSerif(
                            fontSize: 28,
                            fontWeight: FontWeight.w400,
                            color: cineText),
                      ),
                    ),
                  ],
                ),
                if (intro != null) ...[
                  const SizedBox(height: 6),
                  Padding(
                    padding: const EdgeInsets.symmetric(horizontal: 8),
                    child: Text(intro!,
                        style: GoogleFonts.sora(
                            fontSize: 13, height: 1.5, color: cineMuted)),
                  ),
                ],
                const SizedBox(height: 20),
                ...children,
              ],
            ),
          ),
        ),
      ],
    );
    final content = SafeArea(
      child: onRefresh == null
          ? list
          : RefreshIndicator(
              color: cineAccent,
              backgroundColor: cineSheetBg,
              onRefresh: onRefresh!,
              child: list,
            ),
    );

    if (!backdrop) {
      return Scaffold(backgroundColor: Colors.transparent, body: content);
    }
    return Scaffold(
      backgroundColor: cineBackground,
      body: GlassBackdrop(animate: animateBackdrop, child: content),
    );
  }
}

final _stillMouse = ValueNotifier<Offset>(Offset.zero);

/// The app's drifting-particle background and grain behind [child], for full-screen pages outside the nav shell
/// (sign-in, the first-launch question, top-level sub-pages).
class GlassBackdrop extends StatelessWidget {
  const GlassBackdrop({super.key, required this.child, this.animate = true});

  final Widget child;

  /// False in widget tests: the animated background never settles, so a still frame of it is painted instead.
  final bool animate;

  @override
  Widget build(BuildContext context) => Stack(
        children: [
          Positioned.fill(
            child: animate
                ? InteractiveBackgroundLayer(mouseNotifier: _stillMouse)
                // One still frame of the same background (tests: an animation never settles).
                : RepaintBoundary(
                    child: CustomPaint(
                      painter: DeepWaterCurrentPainter(
                          time: 0, mousePos: Offset.zero),
                      size: Size.infinite,
                    ),
                  ),
          ),
          const Positioned.fill(child: NoiseOverlay(opacity: 0.1)),
          Positioned.fill(child: child),
        ],
      );
}

/// A glass card with the app's section header: an accent icon and a bold Sora title.
class GlassSection extends StatelessWidget {
  const GlassSection({
    super.key,
    required this.icon,
    required this.title,
    required this.children,
    this.trailing,
    this.highlight = false,
  });

  final IconData icon;
  final String title;
  final Widget? trailing;
  final List<Widget> children;

  /// A soft accent border, for the card that holds the page's main action.
  final bool highlight;

  @override
  Widget build(BuildContext context) {
    return GlassCard(
      margin: const EdgeInsets.only(bottom: 16),
      borderRadius: 14,
      borderColor: highlight ? cineAccent.withValues(alpha: 0.3) : null,
      glowOpacity: highlight ? 0.08 : 0.03,
      // A see-through Material so list tiles and ink on the card paint above its fill.
      child: Material(
        type: MaterialType.transparency,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Icon(icon, size: 20, color: cineAccent),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(title,
                      style: GoogleFonts.sora(
                          fontSize: 16,
                          fontWeight: FontWeight.w700,
                          color: cineText)),
                ),
                if (trailing != null) trailing!,
              ],
            ),
            const SizedBox(height: 14),
            ...children,
          ],
        ),
      ),
    );
  }
}

/// The main action on a card: a solid accent button.
class GlassPrimaryButton extends StatelessWidget {
  const GlassPrimaryButton(
      {super.key,
      required this.label,
      this.onPressed,
      this.icon,
      this.expand = false,
      this.busy = false});

  final String label;
  final IconData? icon;
  final VoidCallback? onPressed;
  final bool expand;

  /// Shows a small spinner in place of the icon while the action runs (pass a null [onPressed] too).
  final bool busy;

  @override
  Widget build(BuildContext context) {
    final enabled = onPressed != null;
    final fg = enabled ? Colors.white : cineAccent.withValues(alpha: 0.5);
    return Semantics(
      button: true,
      enabled: enabled,
      child: Material(
        color: enabled
            ? cineAccent.withValues(alpha: 0.85)
            : cineAccent.withValues(alpha: 0.15),
        borderRadius: BorderRadius.circular(10),
        child: InkWell(
          onTap: onPressed,
          borderRadius: BorderRadius.circular(10),
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 13),
            child: Row(
              mainAxisSize: expand ? MainAxisSize.max : MainAxisSize.min,
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                if (busy) ...[
                  SizedBox(
                    width: 16,
                    height: 16,
                    child: CircularProgressIndicator(strokeWidth: 2, color: fg),
                  ),
                  const SizedBox(width: 8),
                ] else if (icon != null) ...[
                  Icon(icon, size: 18, color: fg),
                  const SizedBox(width: 8),
                ],
                Flexible(
                  child: Text(label,
                      textAlign: TextAlign.center,
                      style: GoogleFonts.sora(
                          fontSize: 13,
                          fontWeight: FontWeight.w600,
                          color: fg)),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// A secondary action: a glass button with an accent label.
class GlassSecondaryButton extends StatelessWidget {
  const GlassSecondaryButton(
      {super.key, required this.label, this.onPressed, this.icon, this.color});

  final String label;
  final IconData? icon;
  final VoidCallback? onPressed;
  final Color? color;

  @override
  Widget build(BuildContext context) {
    final enabled = onPressed != null;
    final c = (color ?? cineAccent).withValues(alpha: enabled ? 1 : 0.4);
    return Semantics(
      button: true,
      enabled: enabled,
      child: Material(
        color: Colors.transparent,
        child: InkWell(
          onTap: onPressed,
          borderRadius: BorderRadius.circular(10),
          child: Ink(
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
            decoration: BoxDecoration(
              color: cineCardBg,
              borderRadius: BorderRadius.circular(10),
              border: Border.all(color: cineCardBorder),
            ),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                if (icon != null) ...[
                  Icon(icon, size: 18, color: c),
                  const SizedBox(width: 8),
                ],
                Flexible(
                  child: Text(label,
                      style: GoogleFonts.sora(
                          fontSize: 13, fontWeight: FontWeight.w600, color: c)),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// A quiet text action ("Use a pairing code instead", "Unlink").
class GlassTextAction extends StatelessWidget {
  const GlassTextAction(
      {super.key, required this.label, this.onPressed, this.color});

  final String label;
  final VoidCallback? onPressed;
  final Color? color;

  @override
  Widget build(BuildContext context) {
    final c = color ?? cineAccent;
    return TextButton(
      onPressed: onPressed,
      style: TextButton.styleFrom(
        foregroundColor: c,
        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 10),
        minimumSize: const Size(0, 40),
      ),
      child: Text(label,
          style: GoogleFonts.sora(
              fontSize: 12,
              fontWeight: FontWeight.w600,
              color: onPressed == null
                  ? c.withValues(alpha: 0.4)
                  : c.withValues(alpha: 0.85))),
    );
  }
}

/// A text field in the app's glass style.
class GlassTextField extends StatelessWidget {
  const GlassTextField({
    super.key,
    this.fieldKey,
    required this.controller,
    required this.label,
    this.enabled = true,
    this.onSubmitted,
    this.autocorrect = true,
    this.textCapitalization = TextCapitalization.none,
    this.autofocus = false,
    this.hint,
    this.keyboardType,
    this.prefixText,
  });

  /// Put on the inner [TextField], so tests and callers can find the field itself.
  final Key? fieldKey;
  final TextEditingController controller;
  final String label;
  final String? hint;
  final bool enabled;
  final bool autocorrect;
  final bool autofocus;
  final TextCapitalization textCapitalization;
  final ValueChanged<String>? onSubmitted;
  final TextInputType? keyboardType;
  final String? prefixText;

  @override
  Widget build(BuildContext context) {
    return TextField(
      key: fieldKey,
      controller: controller,
      keyboardType: keyboardType,
      enabled: enabled,
      autocorrect: autocorrect,
      enableSuggestions: autocorrect,
      autofocus: autofocus,
      textCapitalization: textCapitalization,
      onSubmitted: onSubmitted,
      style: GoogleFonts.sora(fontSize: 14, color: cineText),
      decoration: InputDecoration(
        labelText: label,
        hintText: hint,
        prefixText: prefixText,
        prefixStyle: GoogleFonts.sora(fontSize: 14, color: cineMuted),
        labelStyle: GoogleFonts.sora(fontSize: 13, color: cineMuted),
        hintStyle: GoogleFonts.sora(
            fontSize: 13, color: cineMuted.withValues(alpha: 0.5)),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(10),
          borderSide: const BorderSide(color: cineCardBorder),
        ),
        disabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(10),
          borderSide: const BorderSide(color: cineCardBorder),
        ),
        focusedBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(10),
          borderSide: BorderSide(color: cineAccent.withValues(alpha: 0.6)),
        ),
        filled: true,
        fillColor: cineCardBg,
        contentPadding:
            const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
      ),
    );
  }
}

enum GlassNoteTone { info, warning, error, success }

/// A calm inline note inside a card. Expected states (not linked, not set up, nothing yet) use [GlassNoteTone.info];
/// red [GlassNoteTone.error] is only for a real failure.
class GlassNote extends StatelessWidget {
  const GlassNote(
      {super.key,
      required this.text,
      this.tone = GlassNoteTone.info,
      this.icon});

  final String text;
  final GlassNoteTone tone;
  final IconData? icon;

  Color get _color => switch (tone) {
        GlassNoteTone.info => cineAccent,
        GlassNoteTone.warning => cineWarning,
        GlassNoteTone.error => cineExpense,
        GlassNoteTone.success => cineIncome,
      };

  IconData get _icon =>
      icon ??
      switch (tone) {
        GlassNoteTone.info => Icons.info_outline_rounded,
        GlassNoteTone.warning => Icons.warning_amber_rounded,
        GlassNoteTone.error => Icons.error_outline_rounded,
        GlassNoteTone.success => Icons.check_circle_outline_rounded,
      };

  @override
  Widget build(BuildContext context) {
    final c = _color;
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: c.withValues(alpha: 0.08),
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: c.withValues(alpha: 0.2)),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(_icon, size: 18, color: c),
          const SizedBox(width: 10),
          Expanded(
            child: Text(
              text,
              style: GoogleFonts.sora(
                fontSize: 12,
                height: 1.45,
                color: tone == GlassNoteTone.info
                    ? cineText.withValues(alpha: 0.85)
                    : c,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// The page's loading spinner, in the accent color.
class GlassSpinner extends StatelessWidget {
  const GlassSpinner({super.key});

  @override
  Widget build(BuildContext context) => const Center(
        child: Padding(
          padding: EdgeInsets.all(12),
          child: SizedBox(
            width: 24,
            height: 24,
            child: CircularProgressIndicator(strokeWidth: 2, color: cineAccent),
          ),
        ),
      );
}

/// A hairline between rows on a glass card.
class GlassDivider extends StatelessWidget {
  const GlassDivider({super.key, this.height = 24});

  final double height;

  @override
  Widget build(BuildContext context) =>
      Divider(color: cineCardBorder, height: height);
}

/// A small rounded status label ("Works", "Limited", "Off").
class GlassStatusPill extends StatelessWidget {
  const GlassStatusPill({super.key, required this.label, required this.color});

  final String label;
  final Color color;

  @override
  Widget build(BuildContext context) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
        decoration: BoxDecoration(
          color: color.withValues(alpha: 0.12),
          borderRadius: BorderRadius.circular(999),
          border: Border.all(color: color.withValues(alpha: 0.35)),
        ),
        child: Text(label,
            style: GoogleFonts.sora(
                fontSize: 11, fontWeight: FontWeight.w600, color: color)),
      );
}

/// A block of config or commands in Fira Code on a dark inset, selectable so it can be copied.
class GlassCodeBlock extends StatelessWidget {
  const GlassCodeBlock({super.key, required this.text});

  final String text;

  @override
  Widget build(BuildContext context) => Container(
        width: double.infinity,
        padding: const EdgeInsets.all(14),
        decoration: BoxDecoration(
          color: const Color(0x66000000),
          borderRadius: BorderRadius.circular(10),
          border: Border.all(color: cineCardBorder),
        ),
        child: SelectableText(
          text,
          style: GoogleFonts.firaCode(
              fontSize: 12,
              height: 1.55,
              color: cineText.withValues(alpha: 0.85)),
        ),
      );
}

/// A label and its value on a glass card; the value is selectable.
class GlassKeyValueRow extends StatelessWidget {
  const GlassKeyValueRow(
      {super.key, required this.label, required this.value, this.mono = false});

  final String label;
  final String value;

  /// Fira Code for addresses, model names and other machine values.
  final bool mono;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 5),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            SizedBox(width: 120, child: Text(label, style: cineMutedStyle())),
            Expanded(
              child: SelectableText(
                value,
                style: mono
                    ? GoogleFonts.firaCode(fontSize: 12.5, color: cineText)
                    : GoogleFonts.sora(fontSize: 13, color: cineText),
              ),
            ),
          ],
        ),
      );
}

/// A setting with a title, a line of explanation and a switch. The whole row toggles it.
class GlassSwitchRow extends StatelessWidget {
  const GlassSwitchRow({
    super.key,
    required this.title,
    required this.subtitle,
    required this.value,
    required this.onChanged,
    this.busy = false,
  });

  final String title;
  final String subtitle;
  final bool value;
  final ValueChanged<bool> onChanged;

  /// Saving: a spinner replaces the switch and taps are ignored.
  final bool busy;

  @override
  Widget build(BuildContext context) => MergeSemantics(
        child: InkWell(
          onTap: busy ? null : () => onChanged(!value),
          borderRadius: BorderRadius.circular(10),
          child: Padding(
            padding: const EdgeInsets.symmetric(vertical: 8),
            child: Row(
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(title, style: cineRowTitleStyle()),
                      const SizedBox(height: 2),
                      Text(subtitle, style: cineMutedStyle()),
                    ],
                  ),
                ),
                const SizedBox(width: 12),
                if (busy)
                  const SizedBox(
                    width: 24,
                    height: 24,
                    child: CircularProgressIndicator(
                        strokeWidth: 2, color: cineAccent),
                  )
                else
                  Switch(
                    value: value,
                    onChanged: onChanged,
                    activeThumbColor: cineAccent,
                    activeTrackColor: cineAccent.withValues(alpha: 0.3),
                    inactiveThumbColor: cineMuted,
                    inactiveTrackColor: cineMuted.withValues(alpha: 0.2),
                  ),
              ],
            ),
          ),
        ),
      );
}

/// A page that could not load: a calm error note and a retry button.
class GlassLoadError extends StatelessWidget {
  const GlassLoadError(
      {super.key,
      required this.message,
      required this.retryLabel,
      required this.onRetry});

  final String message;
  final String retryLabel;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          GlassNote(text: message, tone: GlassNoteTone.error),
          const SizedBox(height: 12),
          GlassSecondaryButton(
              label: retryLabel,
              icon: Icons.refresh_rounded,
              onPressed: onRetry),
        ],
      );
}
