import 'dart:async';
import 'dart:convert';

/// One decoded server-sent event: its `event:` type (if any) and its `data:` payload.
class SseEvent {
  const SseEvent(this.type, this.data);

  /// Value of the `event:` line, or null when the frame carried only `data:`.
  final String? type;

  /// Raw text after `data: `, before JSON decoding.
  final String data;
}

/// Parses an SSE byte stream into events.
///
/// Exists as its own unit because the chat page previously did this inline with
/// `chunk.split('\n')`, which is only correct if every frame happens to land inside a
/// single chunk. It does not: the transport splits wherever it likes, so a long
/// `data: {...}` line is routinely cut in half. The first half parsed as truncated JSON
/// and was silently dropped by a bare `catch`, and the second half did not start with
/// `data: ` so it was ignored — losing the entire frame.
///
/// That is why a 4,283-character answer rendered as an empty bubble in production while
/// shorter and longer ones either side of it were fine: it depended purely on where the
/// chunk boundary fell.
///
/// [LineSplitter] buffers across chunk boundaries and only emits complete lines, which is
/// the property the hand-rolled split was missing.
Stream<SseEvent> parseSseStream(
  Stream<List<int>> byteStream, {
  void Function(String rawLine, Object error)? onMalformed,
}) async* {
  String? currentEventType;

  await for (final line
      in byteStream.transform(utf8.decoder).transform(const LineSplitter())) {
    if (line.startsWith('event: ')) {
      currentEventType = line.substring(7).trim();
      continue;
    }

    if (!line.startsWith('data: ')) {
      // Blank line terminates a frame; anything else (comments, keep-alives) is ignored.
      if (line.trim().isEmpty) currentEventType = null;
      continue;
    }

    final data = line.substring(6);
    if (data == '[DONE]') continue;

    yield SseEvent(currentEventType, data);
    currentEventType = null;
  }
}

/// Decodes an event's payload, reporting rather than swallowing a failure.
///
/// The previous `catch (_) {}` meant a dropped frame was indistinguishable from a frame
/// that never arrived, which is why this went unnoticed until a user reported a blank
/// answer. A malformed frame after this change is still non-fatal, but it is visible.
Map<String, dynamic>? decodeSseData(
  String data, {
  void Function(String rawLine, Object error)? onMalformed,
}) {
  try {
    return jsonDecode(data) as Map<String, dynamic>;
  } catch (e) {
    onMalformed?.call(data, e);
    return null;
  }
}
