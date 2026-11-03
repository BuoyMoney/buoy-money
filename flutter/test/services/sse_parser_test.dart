import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/services/sse_parser.dart';

/// Regression tests for the production bug where a 4,283-character assistant reply
/// rendered as an empty bubble.
///
/// The response was generated, billed and stored — the database row had all 4,283
/// characters. Only the client lost it, because the old parser split each transport chunk
/// on newlines independently. A frame straddling a chunk boundary produced truncated JSON
/// (silently dropped) followed by a fragment with no `data: ` prefix (ignored).
void main() {
  /// Feeds the parser a stream of arbitrary byte chunks, exactly as the transport would.
  Stream<List<int>> chunks(List<String> parts) =>
      Stream.fromIterable(parts.map(utf8.encode));

  Future<List<SseEvent>> collect(List<String> parts) =>
      parseSseStream(chunks(parts)).toList();

  test('reads a frame that arrives whole', () async {
    final events = await collect(['event: text\ndata: {"text":"hello"}\n\n']);

    expect(events, hasLength(1));
    expect(events.single.type, 'text');
    expect(decodeSseData(events.single.data)!['text'], 'hello');
  });

  test('reassembles a frame split mid-JSON across chunks', () async {
    // The exact production failure: the split lands inside the JSON payload.
    final events = await collect([
      'event: text\ndata: {"text":"This is a classic financial ',
      'balancing act"}\n\n',
    ]);

    expect(events, hasLength(1), reason: 'the frame must survive the split');
    expect(decodeSseData(events.single.data)!['text'],
        'This is a classic financial balancing act');
  });

  test('reassembles a frame split mid-word, many times over', () async {
    // A long answer crosses many boundaries; every one is a chance to lose it.
    final body = 'x' * 5000;
    final frame = 'event: text\ndata: {"text":"$body"}\n\n';
    final parts = <String>[];
    for (var i = 0; i < frame.length; i += 97) {
      parts.add(frame.substring(i, (i + 97).clamp(0, frame.length)));
    }

    final events = await collect(parts);

    expect(events, hasLength(1));
    expect((decodeSseData(events.single.data)!['text'] as String).length, 5000);
  });

  test('survives a split between the event line and its data line', () async {
    final events = await collect(['event: text\n', 'data: {"text":"hi"}\n\n']);

    expect(events.single.type, 'text');
  });

  test('keeps frames separate when several arrive in one chunk', () async {
    final events = await collect([
      'event: text\ndata: {"text":"one"}\n\n'
          'event: text\ndata: {"text":"two"}\n\n',
    ]);

    expect(events, hasLength(2));
    expect(decodeSseData(events[0].data)!['text'], 'one');
    expect(decodeSseData(events[1].data)!['text'], 'two');
  });

  test('an event type does not leak onto the following frame', () async {
    final events = await collect([
      'event: function_result\ndata: {"functionName":"get_accounts"}\n\n'
          'data: {"text":"narration"}\n\n',
    ]);

    expect(events[0].type, 'function_result');
    expect(events[1].type, isNull,
        reason: 'a bare data frame must not inherit the previous event type');
  });

  test('[DONE] is not surfaced as an event', () async {
    final events = await collect(['data: [DONE]\n\n']);

    expect(events, isEmpty);
  });

  test('handles CRLF line endings', () async {
    final events = await collect(['event: text\r\ndata: {"text":"hi"}\r\n\r\n']);

    expect(events.single.type, 'text');
    expect(decodeSseData(events.single.data)!['text'], 'hi');
  });

  test('a malformed payload is reported, not silently swallowed', () async {
    // The old bare `catch (_) {}` is why this class of loss went unnoticed until a user
    // reported a blank answer.
    Object? reported;
    final result = decodeSseData('{not json', onMalformed: (_, e) => reported = e);

    expect(result, isNull);
    expect(reported, isNotNull);
  });

  test('a connection cut mid-frame surfaces as malformed, not as silence', () async {
    // LineSplitter emits the trailing partial line when a stream ends without a
    // terminator, so a truncated frame DOES come through — and its JSON does not parse.
    //
    // Left that way deliberately rather than suppressed. Dropping it quietly would
    // recreate exactly the blindness that hid this bug: a half-delivered answer would look
    // identical to no answer at all. Surfacing it makes the next occurrence diagnosable.
    final events = await collect(['event: text\ndata: {"text":"cut off here']);

    expect(events, hasLength(1));

    Object? reported;
    final decoded =
        decodeSseData(events.single.data, onMalformed: (_, e) => reported = e);

    expect(decoded, isNull, reason: 'truncated JSON must not be treated as content');
    expect(reported, isNotNull, reason: 'and the caller must be told');
  });
}
