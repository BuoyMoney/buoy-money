import '../../config/app_brand.dart';
import 'dart:ui';
import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import '../../l10n/generated/app_localizations.dart';

class CinematicFooter extends StatelessWidget {
  const CinematicFooter({super.key});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return ClipRRect(
      borderRadius: const BorderRadius.only(
        topLeft: Radius.circular(64),
        topRight: Radius.circular(64),
      ),
      child: BackdropFilter(
        // Gentle blur so the animated background dots remain faintly visible
        // through the footer (glassmorphic, not frosted-opaque).
        filter: ImageFilter.blur(sigmaX: 6, sigmaY: 6),
        child: Container(
          width: double.infinity,
          padding: const EdgeInsets.only(
              top: 48, bottom: 28, left: 40, right: 40),
          decoration: BoxDecoration(
            // Translucent white tint — consistent with canonical GlassCard.
            color: Colors.white.withValues(alpha: 0.04),
            borderRadius: const BorderRadius.only(
              topLeft: Radius.circular(64),
              topRight: Radius.circular(64),
            ),
            border: Border(
              top: BorderSide(
                color: Colors.white.withValues(alpha: 0.12),
              ),
            ),
          ),
          child: Column(
        children: [
          Row(
            mainAxisAlignment: MainAxisAlignment.spaceBetween,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    '${AppBrand.name}.',
                    style: theme.textTheme.headlineLarge?.copyWith(
                      fontWeight: FontWeight.w800,
                      letterSpacing: -1,
                    ),
                  ),
                  const SizedBox(height: 16),
                  Text(
                    AppLocalizations.of(context).uiFooterTagline,
                    style: theme.textTheme.bodyMedium?.copyWith(color: theme.colorScheme.onSurface.withValues(alpha: 0.6)),
                  ),
                ],
              ),
              
              // Links
              Expanded(
                child: Wrap(
                  alignment: WrapAlignment.end,
                  spacing: 64,
                  runSpacing: 40,
                  children: [
                    Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(AppLocalizations.of(context).uiFooterLegal, style: theme.textTheme.bodyMedium?.copyWith(fontWeight: FontWeight.bold)),
                        const SizedBox(height: 16),
                        InkWell(
                          onTap: () => context.go('/privacy-policy'),
                          child: Text(AppLocalizations.of(context).uiPrivacyPolicy, style: theme.textTheme.bodySmall?.copyWith(color: theme.colorScheme.onSurface.withValues(alpha: 0.7)))
                        ),
                        const SizedBox(height: 8),
                        InkWell(
                          onTap: () => context.go('/terms'),
                          child: Text(AppLocalizations.of(context).uiTermsOfService, style: theme.textTheme.bodySmall?.copyWith(color: theme.colorScheme.onSurface.withValues(alpha: 0.7)))
                        ),
                      ],
                    ),
                  ],
                ),
              )
            ],
          ),
          const SizedBox(height: 32),
          Divider(color: theme.colorScheme.onSurface.withValues(alpha: 0.1)),
          const SizedBox(height: 20),
          Wrap(
            alignment: WrapAlignment.spaceBetween,
            runAlignment: WrapAlignment.center,
            crossAxisAlignment: WrapCrossAlignment.center,
            spacing: 16,
            runSpacing: 12,
            children: [
              Text(
                AppLocalizations.of(context).uiCopyright,
                style: theme.textTheme.bodySmall?.copyWith(color: theme.colorScheme.onSurface.withValues(alpha: 0.5)),
              ),
              Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(Icons.lock_outline, size: 16, color: theme.colorScheme.onSurface.withValues(alpha: 0.5)),
                  const SizedBox(width: 8),
                  Text(
                    AppLocalizations.of(context).uiBankLevelSecurity,
                    style: theme.textTheme.bodySmall?.copyWith(color: theme.colorScheme.onSurface.withValues(alpha: 0.5)),
                  ),
                ],
              )
            ],
          ),
        ],
      ),
        ),
      ),
    );
  }
}
