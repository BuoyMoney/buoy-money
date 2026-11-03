import 'package:flutter/foundation.dart';
import 'package:flutter_timezone/flutter_timezone.dart';

/// Best-effort IANA timezone id (e.g. "America/Chicago") for push scheduling.
/// The backend prefers this over the minute offset because a zone id follows
/// DST instantly instead of drifting until the next app start (#941). Null on
/// platforms/failures where it can't be resolved — the offset remains the
/// fallback on both ends.
Future<String?> deviceTimeZoneId() async {
  try {
    final id = await FlutterTimezone.getLocalTimezone();
    return id.identifier.isEmpty ? null : id.identifier;
  } catch (e) {
    debugPrint('Timezone id lookup failed: $e');
    return null;
  }
}
