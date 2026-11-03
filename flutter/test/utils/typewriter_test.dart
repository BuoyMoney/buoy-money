import 'package:buoy_ai/utils/typewriter.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('Typewriter.take', () {
    test('takes one word with its trailing whitespace', () {
      expect(Typewriter.take('Hello there world', 1), 'Hello ');
    });

    test('takes several words and keeps newlines attached', () {
      expect(Typewriter.take('one two\n\nthree four', 2), 'one two\n\n');
    });

    test('returns the remainder when fewer words are left', () {
      expect(Typewriter.take('last', 3), 'last');
    });

    test('empty buffer yields empty string', () {
      expect(Typewriter.take('', 2), '');
    });

    test('never splits inside a markdown token', () {
      // "**$1,900.00**" is one word to the splitter, so bold markers stay paired.
      expect(Typewriter.take('Rent **\$1,900.00** is paid.', 2), 'Rent **\$1,900.00** ');
    });

    test('draining word by word reproduces the original text exactly', () {
      const text = 'Key Takeaways:\n1. Fixed costs covered. 2. Variable spending is minimal.';
      var pending = text;
      final out = StringBuffer();
      while (pending.isNotEmpty) {
        final chunk = Typewriter.take(pending, 1);
        out.write(chunk);
        pending = pending.substring(chunk.length);
      }
      expect(out.toString(), text);
    });
  });

  group('Typewriter.wordsPerTick', () {
    test('speeds up for long answers', () {
      expect(Typewriter.wordsPerTick(50), 1);
      expect(Typewriter.wordsPerTick(300), 1);
      expect(Typewriter.wordsPerTick(600), 2);
      expect(Typewriter.wordsPerTick(1500), 3);
    });
  });
}
