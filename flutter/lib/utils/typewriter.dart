/// Pacing for the word-by-word reveal of an assistant answer.
///
/// The backend delivers the whole answer in one SSE frame, so the reveal is a
/// client-side effect: text lands in a pending buffer and a timer moves a few
/// words at a time into the visible bubble. Splitting on words (with their
/// trailing whitespace) keeps markdown tokens intact and reads naturally.
class Typewriter {
  Typewriter._();

  static final _word = RegExp(r'\S+\s*');

  /// How many words to reveal per tick for a buffer of [pendingLength]
  /// characters: one word for short answers, more when a long answer would
  /// otherwise take too long to finish.
  /// At a 50 ms tick that is 20, 40 or 60 words a second: readable while it
  /// runs, and a long answer still lands in well under ten seconds.
  static int wordsPerTick(int pendingLength) {
    if (pendingLength > 1200) return 3;
    if (pendingLength > 300) return 2;
    return 1;
  }

  /// The next [words] words of [pending] (whitespace attached), or the whole
  /// remainder when fewer are left. Returns the empty string for an empty buffer.
  static String take(String pending, int words) {
    var end = 0;
    for (var i = 0; i < words; i++) {
      final m = _word.matchAsPrefix(pending, end);
      if (m == null) {
        // Leading whitespace with no word after it: flush it.
        return pending.substring(0, pending.length);
      }
      end = m.end;
      if (end >= pending.length) break;
    }
    return pending.substring(0, end);
  }
}
