import 'package:flutter/material.dart';

import '../providers/auth_provider.dart';

/// Holds the routed app back until the startup session restore finishes.
///
/// On a browser reload the access token is memory-only, so the first thing the app
/// does is exchange the refresh cookie for a new one. If a deep link such as
/// /app/dashboard builds during that window, its data calls go out with no token,
/// come back 401, and used to sign the user out with a false "session expired".
/// Not mounting the Navigator until the restore resolves means protected pages
/// only ever build once the session state is known. The router keeps the deep
/// link location the whole time, so the user lands where they asked to.
class AuthRestoreGate extends StatelessWidget {
  const AuthRestoreGate({super.key, required this.auth, required this.child});

  final AuthProvider auth;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: auth,
      builder: (context, _) {
        if (!auth.isRestoringSession) return child;
        return const ColoredBox(
          color: Color(0xFF04040A),
          child: Center(
            child: CircularProgressIndicator(
              color: Color(0xFF7B61FF),
              strokeWidth: 2,
            ),
          ),
        );
      },
    );
  }
}
