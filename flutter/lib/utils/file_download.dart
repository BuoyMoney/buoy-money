/// Saves or shares a downloaded file. Web triggers a browser download (Blob +
/// anchor); mobile opens the share sheet. The conditional export keeps
/// mobile-only plugins out of the web build.
library;

export 'file_download_mobile.dart'
    if (dart.library.js_interop) 'file_download_web.dart';
