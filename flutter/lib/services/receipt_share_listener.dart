import 'dart:async';

import 'package:image_picker/image_picker.dart' show XFile;
import 'package:flutter/foundation.dart' show defaultTargetPlatform, kIsWeb, TargetPlatform;
import 'package:flutter/widgets.dart';
import 'package:receive_sharing_intent/receive_sharing_intent.dart';
import '../widgets/app_toast.dart';
import '../widgets/receipt_scan_sheet.dart';
import 'receipts_service.dart';

/// Share → BuoyAI. When the user shares a screenshot, photo or PDF to BuoyAI from another app,
/// the OS opens the app with the files; this opens the receipt sheet with them. The sheet shows
/// the files and sends nothing until the user taps Send, so a share is never an upload by itself.
///
/// Android only for now (the manifest's SEND intent filters). iOS needs a share extension target.
class ReceiptShareListener {
  ReceiptShareListener._();

  static StreamSubscription<List<SharedMediaFile>>? _sub;
  static bool _handling = false;

  /// The signed-in shell's context, replaced on every attach so a share never reaches a disposed screen.
  static BuildContext Function()? _context;

  static bool get supported => !kIsWeb && defaultTargetPlatform == TargetPlatform.android;

  /// Call from the signed-in shell, with a context under the app's Navigator.
  static Future<void> attach(BuildContext Function() context) async {
    if (!supported) return;
    _context = context;
    _sub ??= ReceiveSharingIntent.instance.getMediaStream().listen(_handle);
    try {
      // A share that launched the app, or arrived while signed out, is still waiting here.
      await _handle(await ReceiveSharingIntent.instance.getInitialMedia());
    } catch (_) {
      // No launch share (or the plugin isn't available in this build).
    }
  }

  static Future<void> detach() async {
    _context = null;
    await _sub?.cancel();
    _sub = null;
  }

  static Future<void> _handle(List<SharedMediaFile> shared) async {
    if (shared.isEmpty || _handling) return;
    final ctx = _context?.call();
    // Signed out (no shell): leave the share in place for the next attach.
    if (ctx == null || !ctx.mounted) return;
    _handling = true;
    try {
      final files = await readReceiptFiles(shared.where(_isReceiptFile).map((f) => XFile(f.path)));
      // Cleared only once the sheet is about to show, so a share is never dropped unseen.
      ReceiveSharingIntent.instance.reset();
      if (!ctx.mounted) return;
      if (files == null) {
        showAppToast(ctx, ReceiptsService.tooLargeMessage, type: AppToastType.error);
        return;
      }
      if (files.isEmpty) return;
      await showReceiptScanSheet(ctx, files);
    } catch (_) {
      // An unreadable share just doesn't open the sheet.
    } finally {
      _handling = false;
    }
  }

  static bool _isReceiptFile(SharedMediaFile f) {
    final mime = (f.mimeType ?? '').toLowerCase();
    final path = f.path.toLowerCase();
    return mime.startsWith('image/') ||
        mime == 'application/pdf' ||
        f.type == SharedMediaType.image ||
        path.endsWith('.pdf') ||
        path.endsWith('.png') ||
        path.endsWith('.jpg') ||
        path.endsWith('.jpeg') ||
        path.endsWith('.heic') ||
        path.endsWith('.webp');
  }
}
