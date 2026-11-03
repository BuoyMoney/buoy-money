# Flutter-specific ProGuard rules
# Keep Flutter wrapper classes
-keep class io.flutter.app.** { *; }
-keep class io.flutter.plugin.** { *; }
-keep class io.flutter.util.** { *; }
-keep class io.flutter.view.** { *; }
-keep class io.flutter.** { *; }
-keep class io.flutter.plugins.** { *; }

# Keep Google Sign-In classes
-keep class com.google.android.gms.auth.** { *; }
-keep class com.google.android.gms.common.** { *; }

# Keep AndroidX Credential Manager + Google ID provider classes.
#
# google_sign_in 7.x routes Android sign-in through the AndroidX Credential
# Manager. The actual credential provider (androidx.credentials.playservices.*)
# is discovered reflectively at runtime; with isMinifyEnabled/isShrinkResources
# on, R8 strips it from release builds, so getCredentialAsync throws
# "GoogleSignInException(code providerConfigurationError, ... no provider
# dependencies found)". This crashed release builds only (debug worked) — 24x
# fatal in prod. Keep the Credential Manager API, its Play Services provider
# implementation, and the Google ID token classes so the provider is found.
-keep class androidx.credentials.** { *; }
-keep class androidx.credentials.playservices.** { *; }
-keep class com.google.android.libraries.identity.googleid.** { *; }
-dontwarn androidx.credentials.**
-dontwarn com.google.android.libraries.identity.googleid.**

# Keep Plaid SDK classes
-keep class com.plaid.** { *; }

# Keep Dio/OkHttp classes for networking
-dontwarn okhttp3.**
-dontwarn okio.**
-keep class okhttp3.** { *; }
-keep class okio.** { *; }

# Keep JSON serialization classes
-keepattributes Signature
-keepattributes *Annotation*
-keep class * extends com.google.gson.TypeAdapter
-keep class * implements com.google.gson.TypeAdapterFactory
-keep class * implements com.google.gson.JsonSerializer
-keep class * implements com.google.gson.JsonDeserializer

# Keep model classes for JSON parsing
-keep class com.buoyai.buoy_ai.models.** { *; }

# Flutter Play Store split-install classes — these are referenced by
# the Flutter engine but only present at runtime when the app is
# actually installed via Play Store. R8 needs them either kept or
# explicitly ignored to stop release builds from failing.
# https://github.com/flutter/flutter/issues/116182
-dontwarn com.google.android.play.core.**
-keep class com.google.android.play.core.** { *; }
-keep class io.flutter.embedding.engine.deferredcomponents.** { *; }
-keep class io.flutter.embedding.android.FlutterPlayStoreSplitApplication { *; }
