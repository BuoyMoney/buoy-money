#!/bin/sh

# Xcode Cloud post-clone script for the BuoyAI Flutter app.
#
# Xcode Cloud machines do NOT have Flutter installed, and this repository keeps
# the Flutter app in /flutter with the Xcode workspace at /flutter/ios. This
# script installs Flutter, fetches packages, generates the iOS build config the
# Xcode "Run Script" phase expects, and installs CocoaPods — so Xcode Cloud can
# then archive and (with the signing it manages) ship to TestFlight.
#
# This folder lives next to Runner.xcworkspace so Xcode Cloud discovers it.
# NOTE: This is a starting template — the first Xcode Cloud run may need a tweak
# (Flutter version, paths). Watch that run's logs.

set -e

echo "▸ Installing Flutter (stable)…"
git clone https://github.com/flutter/flutter.git --depth 1 --branch stable "$HOME/flutter"
export PATH="$HOME/flutter/bin:$PATH"

flutter --version
flutter precache --ios

echo "▸ Fetching Dart/Flutter packages…"
cd "$CI_PRIMARY_REPOSITORY_PATH/flutter"
flutter pub get

# Builds the Dart/Flutter artifacts and generates ios/Flutter/Generated.xcconfig
# (which the Xcode build phase requires). --no-codesign because Xcode Cloud
# performs signing during its own archive step.
echo "▸ Pre-building iOS Flutter artifacts…"
flutter build ios --release --no-codesign

echo "▸ Installing CocoaPods dependencies…"
cd ios
pod install

echo "✅ ci_post_clone complete."
