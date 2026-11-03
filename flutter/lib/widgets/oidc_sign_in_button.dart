import 'package:flutter/material.dart';

/// "Sign in with (provider)" for a self-hosted server's single sign-on. Matches the height and
/// radius of the Google and Apple buttons so the three stack cleanly.
class OidcSignInButton extends StatelessWidget {
  const OidcSignInButton({
    super.key,
    required this.label,
    required this.onPressed,
    this.isLoading = false,
    this.width,
  });

  final String label;
  final VoidCallback? onPressed;
  final bool isLoading;
  final double? width;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Center(
      child: SizedBox(
        width: width,
        height: 44,
        child: OutlinedButton.icon(
          key: const Key('oidc-sign-in-button'),
          onPressed: isLoading ? null : onPressed,
          style: OutlinedButton.styleFrom(
            foregroundColor: theme.colorScheme.onSurface,
            side: BorderSide(
                color: theme.colorScheme.onSurface.withValues(alpha: 0.25)),
            shape:
                RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
          ),
          icon: isLoading
              ? const SizedBox(
                  width: 16,
                  height: 16,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              : Icon(Icons.key_rounded,
                  size: 20, color: theme.colorScheme.secondary),
          label: Text(
            label,
            overflow: TextOverflow.ellipsis,
            style: const TextStyle(fontWeight: FontWeight.w600),
          ),
        ),
      ),
    );
  }
}
