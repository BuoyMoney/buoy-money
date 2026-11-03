import '../../l10n/generated/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_markdown/flutter_markdown.dart';
import 'package:flutter_svg/flutter_svg.dart';
import 'package:google_fonts/google_fonts.dart';

/// #388 Phase 2 — renders an assistant message that may contain MODEL-AUTHORED
/// visualizations. When the model emits a fenced ```svg block, we render that SVG
/// natively (flutter_svg) instead of as a code block — the "agent draws the view"
/// direction, but cross-platform (web + mobile) and SAFE: flutter_svg executes no
/// scripts and loads no external resources, so untrusted SVG can't run code.
///
/// Everything else renders as normal Markdown. The message is split into ordered
/// text / svg segments and laid out in sequence.
class AiRichMessage extends StatelessWidget {
  final String content;
  final MarkdownStyleSheet styleSheet;

  /// Off while an answer is still being revealed: a selectable text that keeps
  /// changing asks the viewport to show its caret every rebuild, which idles the
  /// chat list's follow-scroll before it can move.
  final bool selectable;

  /// The answer is still arriving: a chart whose closing fence hasn't come yet
  /// shows its spinner. Once the answer is complete, an unclosed fence renders as
  /// written.
  final bool streaming;

  const AiRichMessage({
    super.key,
    required this.content,
    required this.styleSheet,
    this.selectable = true,
    this.streaming = false,
  });

  // ```svg ... ``` fenced blocks (multiline, non-greedy).
  static final RegExp _svgFence =
      RegExp(r'```svg\s*\n(.*?)```', dotAll: true, caseSensitive: false);

  // An opening ```svg fence whose closing fence hasn't arrived yet: the answer is
  // still streaming in. Until it closes, the chart's spinner shows in its place;
  // rendered as Markdown it was half an SVG typed out as code.
  static final RegExp _openSvgFence = RegExp(r'```svg', caseSensitive: false);

  @override
  Widget build(BuildContext context) {
    final matches = _svgFence.allMatches(content).toList();
    final tail = content.substring(matches.isEmpty ? 0 : matches.last.end);
    final open = streaming ? _openSvgFence.firstMatch(tail) : null;
    if (matches.isEmpty && open == null) {
      return MarkdownBody(
          data: content, selectable: selectable, styleSheet: styleSheet);
    }

    final children = <Widget>[];
    void addText(String text) {
      if (text.trim().isNotEmpty) {
        children.add(MarkdownBody(
            data: text.trim(), selectable: selectable, styleSheet: styleSheet));
      }
    }

    var cursor = 0;
    for (final m in matches) {
      addText(content.substring(cursor, m.start));
      children.add(_SvgView(svg: m.group(1)!.trim()));
      cursor = m.end;
    }
    if (open != null) {
      addText(tail.substring(0, open.start));
      children.add(const _SvgFrame(child: _SvgSpinner()));
    } else {
      addText(tail);
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        for (var i = 0; i < children.length; i++) ...[
          if (i > 0) const SizedBox(height: 10),
          children[i],
        ],
      ],
    );
  }
}

/// The frame a chart sits in, also used for its spinner while it streams in.
class _SvgFrame extends StatelessWidget {
  final Widget child;
  const _SvgFrame({required this.child});

  @override
  Widget build(BuildContext context) {
    return Container(
      width: double.infinity,
      constraints: const BoxConstraints(maxHeight: 320),
      padding: const EdgeInsets.all(8),
      decoration: BoxDecoration(
        color: const Color(0x0AFFFFFF),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: const Color(0x14FFFFFF)),
      ),
      child: child,
    );
  }
}

class _SvgSpinner extends StatelessWidget {
  const _SvgSpinner();

  @override
  Widget build(BuildContext context) {
    return const SizedBox(
      height: 80,
      child: Center(child: CircularProgressIndicator(strokeWidth: 2)),
    );
  }
}

class _SvgView extends StatelessWidget {
  final String svg;
  const _SvgView({required this.svg});

  @override
  Widget build(BuildContext context) {
    return _SvgFrame(
      child: SvgPicture.string(
        svg,
        fit: BoxFit.contain,
        placeholderBuilder: (_) => const _SvgSpinner(),
        // A broken/unsupported SVG should degrade gracefully, never crash the chat.
        errorBuilder: (_, __, ___) => Text(
          AppLocalizations.of(context).uiCouldNotRenderVisualization,
          style: GoogleFonts.sora(fontSize: 12, color: const Color(0xFF8A8A9A)),
        ),
      ),
    );
  }
}
