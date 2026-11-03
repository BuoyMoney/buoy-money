import 'dart:typed_data';
import 'dart:ui' show Rect;

import 'package:share_plus/share_plus.dart';

/// Opens the system share sheet with the file so the user can save it to Files,
/// Drive, email it, and so on.
Future<void> saveBytesAsFile(
  Uint8List bytes, {
  required String filename,
  required String mimeType,
  Rect? sharePositionOrigin,
}) async {
  await SharePlus.instance.share(ShareParams(
    files: [XFile.fromData(bytes, mimeType: mimeType, name: filename)],
    fileNameOverrides: [filename],
    // iPad needs an anchor rect for the share popover or the sheet fails to open.
    sharePositionOrigin: sharePositionOrigin,
  ));
}
