import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/widgets/ai/ai_rich_message.dart';
import 'package:flutter/material.dart';
import 'package:flutter_markdown/flutter_markdown.dart';
import 'package:flutter_svg/flutter_svg.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';

// An answer with a chart arrives a few words at a time. Until the chart's closing
// fence arrives, its SVG used to render as Markdown: half a chart typed out as code
// under the answer (seen in a screen recording).
void main() {
  setUpAll(() => GoogleFonts.config.allowRuntimeFetching = false);

  const chart =
      '<svg viewBox="0 0 360 140" xmlns="http://www.w3.org/2000/svg"><rect width="360" height="140" fill="#7B61FF"/></svg>';

  Widget wrap(Widget child) => MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(body: SingleChildScrollView(child: child)),
      );

  AiRichMessage message(String content, {bool streaming = false}) =>
      AiRichMessage(
        content: content,
        styleSheet: MarkdownStyleSheet(),
        selectable: !streaming,
        streaming: streaming,
      );

  // Selectable Markdown renders through EditableText, the rest through RichText.
  bool showsCode(WidgetTester tester) =>
      find.textContaining('<svg', findRichText: true).evaluate().isNotEmpty;

  testWidgets('a chart still streaming in shows its spinner, not its code',
      (tester) async {
    await tester.pumpWidget(wrap(message(
        'Weekdays: \$1,059.20\n\n```svg\n<svg viewBox="0 0 360 140" xmlns="http',
        streaming: true)));

    expect(find.textContaining('Weekdays'), findsOneWidget);
    expect(find.byType(CircularProgressIndicator), findsOneWidget);
    expect(showsCode(tester), isFalse);
  });

  testWidgets('a finished chart draws as a picture', (tester) async {
    await tester.pumpWidget(
        wrap(message('Breakdown\n\n```svg\n$chart\n```\n\nThat is all.')));

    expect(find.byType(SvgPicture), findsOneWidget);
    expect(find.textContaining('That is all.'), findsOneWidget);
    expect(showsCode(tester), isFalse);
  });

  testWidgets('a complete answer with an unclosed fence shows what was written',
      (tester) async {
    await tester.pumpWidget(
        wrap(message('Breakdown\n\n```svg\n<svg viewBox="0 0 10 10">')));

    expect(find.byType(CircularProgressIndicator), findsNothing);
    expect(showsCode(tester), isTrue);
  });

  testWidgets('an answer with no chart is plain Markdown', (tester) async {
    await tester.pumpWidget(
        wrap(message('**No**, you spend more on weekdays.', streaming: true)));

    expect(find.byType(MarkdownBody), findsOneWidget);
    expect(find.byType(CircularProgressIndicator), findsNothing);
  });
}
