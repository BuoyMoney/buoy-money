import 'package:flutter/material.dart';
import 'cinematic_legal_page.dart';

class PrivacyPolicyPage extends StatelessWidget {
  const PrivacyPolicyPage({super.key});

  @override
  Widget build(BuildContext context) {
    return const CinematicLegalPage(
      assetPath: 'assets/legal/PRIVACY_POLICY.md',
      heroLabel: 'LEGAL',
      heroTitle: 'Privacy Policy',
      heroDrama: 'Your data. Your control.',
      version: 'Version 1.0',
      effectiveDate: 'Effective: September 9, 2025',
    );
  }
}
