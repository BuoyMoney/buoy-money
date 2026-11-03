import 'package:flutter/material.dart';
import 'package:flutter_svg/flutter_svg.dart';
import 'package:google_fonts/google_fonts.dart';

/// The "Sign in with Google" button used on BOTH the login and register pages.
///
/// Previously each page carried its own private copy of this widget. The copies had
/// already drifted apart in formatting, and nothing stopped them drifting in
/// appearance — which is exactly what makes the two pages look like different
/// products to a user who visits both.
///
/// On web the pages render Google's own `renderButton` instead of this; that widget
/// is web-only, so on mobile this stands in for it. It follows Google's button
/// guidance rather than approximating it: the official four-colour mark (not a
/// letter "G" set in the app's own font), a neutral surface, and the mark sized
/// against the label rather than the button.
class GoogleAuthButton extends StatefulWidget {
  const GoogleAuthButton({
    super.key,
    required this.label,
    required this.onPressed,
    this.isLoading = false,
  });

  /// Google's guidance allows "Sign in with Google" or "Sign up with Google";
  /// the pages differ only here, and only in the verb.
  final String label;
  final VoidCallback? onPressed;
  final bool isLoading;

  @override
  State<GoogleAuthButton> createState() => _GoogleAuthButtonState();
}

class _GoogleAuthButtonState extends State<GoogleAuthButton> {
  bool _hovering = false;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final enabled = widget.onPressed != null && !widget.isLoading;

    return MouseRegion(
      cursor: enabled ? SystemMouseCursors.click : SystemMouseCursors.basic,
      onEnter: (_) => setState(() => _hovering = true),
      onExit: (_) => setState(() => _hovering = false),
      child: GestureDetector(
        onTap: enabled ? widget.onPressed : null,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 150),
          height: 48,
          decoration: BoxDecoration(
            color: theme.colorScheme.onSurface
                .withValues(alpha: _hovering && enabled ? 0.08 : 0.04),
            borderRadius: BorderRadius.circular(16),
            border: Border.all(
              color: theme.colorScheme.onSurface
                  .withValues(alpha: _hovering && enabled ? 0.15 : 0.1),
            ),
          ),
          child: Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              if (widget.isLoading)
                SizedBox(
                  width: 20,
                  height: 20,
                  child: CircularProgressIndicator(
                    strokeWidth: 2,
                    color:
                        theme.colorScheme.onSurface.withValues(alpha: 0.5),
                  ),
                )
              else
                // The official mark. Never recoloured or monochromed — Google's
                // branding terms require the four-colour asset as supplied.
                SvgPicture.asset(
                  'assets/icons/google_g.svg',
                  width: 20,
                  height: 20,
                ),
              const SizedBox(width: 12),
              Text(
                widget.label,
                style: GoogleFonts.sora(
                  fontSize: 15,
                  fontWeight: FontWeight.w500,
                  color: theme.colorScheme.onSurface.withValues(alpha: 0.8),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
