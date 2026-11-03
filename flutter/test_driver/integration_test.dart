// Driver entrypoint for `flutter drive` integration tests.
//
// Pairs with files under integration_test/. Saves any screenshots a test captures
// (binding.takeScreenshot) under flutter/build/viz-screenshots/<name>.png so render
// checks (e.g. the AI visualization widgets, #388) produce real pixels to inspect.
import 'dart:io';

import 'package:integration_test/integration_test_driver_extended.dart';

Future<void> main() async {
  await integrationDriver(
    onScreenshot: (String name, List<int> bytes, [Map<String, Object?>? args]) async {
      final dir = Directory('build/viz-screenshots');
      if (!dir.existsSync()) dir.createSync(recursive: true);
      File('${dir.path}/$name.png').writeAsBytesSync(bytes);
      return true;
    },
  );
}
