// PLACEHOLDER Firebase configuration, shipped in the public source on purpose.
//
// Every apiKey is 'REPLACE-ME'. PushNotificationService checks for that marker and leaves
// Firebase off, so the app builds and runs without a Firebase project. Self-hosted builds get
// phone notifications through the push relay instead (docs/self_host/push_relay.md).
//
// To run your own Firebase project: `dart pub global activate flutterfire_cli`, then
// `cd flutter && flutterfire configure`. That overwrites this file and
// android/app/google-services.json with your values. Restrict the keys to your app's package
// name and signing certificate in the Google Cloud console.

import 'package:firebase_core/firebase_core.dart' show FirebaseOptions;
import 'package:flutter/foundation.dart'
    show defaultTargetPlatform, kIsWeb, TargetPlatform;

class DefaultFirebaseOptions {
  static FirebaseOptions get currentPlatform {
    if (kIsWeb) {
      // Web builds never initialize Firebase (push is mobile-only), but returning the
      // placeholder keeps this getter total instead of throwing.
      return android;
    }
    switch (defaultTargetPlatform) {
      case TargetPlatform.android:
        return android;
      case TargetPlatform.iOS:
        return ios;
      default:
        return android;
    }
  }

  static const FirebaseOptions android = FirebaseOptions(
    apiKey: 'REPLACE-ME',
    appId: '1:000000000000:android:0000000000000000',
    messagingSenderId: '000000000000',
    projectId: 'your-firebase-project',
    storageBucket: 'your-firebase-project.firebasestorage.app',
  );
  static const FirebaseOptions ios = FirebaseOptions(
    apiKey: 'REPLACE-ME',
    appId: '1:000000000000:ios:0000000000000000',
    messagingSenderId: '000000000000',
    projectId: 'your-firebase-project',
    storageBucket: 'your-firebase-project.firebasestorage.app',
    iosBundleId: 'com.buoyai.buoyAi',
  );
}
