import 'package:flutter/material.dart';
import 'cinematic_legal_page.dart';

class TermsOfServicePage extends StatelessWidget {
  const TermsOfServicePage({super.key});

  @override
  Widget build(BuildContext context) {
    return const CinematicLegalPage(
      assetPath: 'assets/legal/TERMS_OF_SERVICE.md',
      heroLabel: 'LEGAL',
      heroTitle: 'Terms of Service',
      heroDrama: 'Fair terms. Clear language.',
      version: 'Version 1.0',
      effectiveDate: 'Effective: January 29, 2026',
    );
  }
}
