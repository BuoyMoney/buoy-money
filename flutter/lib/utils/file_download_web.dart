import 'dart:js_interop';
import 'dart:typed_data';
import 'dart:ui' show Rect;

import 'package:web/web.dart' as web;

/// Triggers a browser download of [bytes] named [filename].
Future<void> saveBytesAsFile(
  Uint8List bytes, {
  required String filename,
  required String mimeType,
  Rect? sharePositionOrigin, // only used by the mobile share sheet
}) async {
  final blob = web.Blob(
    [bytes.toJS].toJS,
    web.BlobPropertyBag(type: mimeType),
  );
  final url = web.URL.createObjectURL(blob);
  final anchor = web.HTMLAnchorElement()
    ..href = url
    ..download = filename
    ..style.display = 'none';
  web.document.body?.append(anchor);
  anchor.click();
  anchor.remove();
  web.URL.revokeObjectURL(url);
}
