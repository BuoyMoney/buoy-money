import 'package:flutter/widgets.dart';
import 'package:google_sign_in_web/web_only.dart';

/// Web implementation: renders Google's official Sign-In button via GIS SDK.
/// Authentication result flows through GoogleSignIn.instance.authenticationEvents.
Widget? buildGoogleSignInWebButton({double? minimumWidth}) => renderButton(
      configuration: GSIButtonConfiguration(
        theme: GSIButtonTheme.outline,
        size: GSIButtonSize.large,
        text: GSIButtonText.continueWith,
        shape: GSIButtonShape.pill,
        // Widen the GIS button to match the Apple button (GIS caps at 400px).
        minimumWidth: minimumWidth,
      ),
    );
