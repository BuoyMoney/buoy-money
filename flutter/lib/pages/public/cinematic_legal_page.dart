import '../../l10n/generated/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart' show rootBundle;
import 'package:flutter_markdown/flutter_markdown.dart';
import 'package:google_fonts/google_fonts.dart';
import '../../widgets/cinematic/noise_overlay.dart';
import '../../widgets/cinematic/cinematic_footer.dart';
import '../../widgets/cinematic/backgrounds/interactive_background_layer.dart';
import '../../utils/web_navigation.dart';

/// A reusable cinematic-styled legal page that renders markdown from an asset file.
///
/// The content source of truth is `legal/*.md` — synced to `assets/legal/`
/// by `scripts/sync-legal.sh`. This widget loads and renders that markdown
/// with the cinematic design language (VaporClinic theme).
class CinematicLegalPage extends StatefulWidget {
  final String assetPath;
  final String heroLabel;
  final String heroTitle;
  final String heroDrama;
  final String version;
  final String effectiveDate;

  const CinematicLegalPage({
    super.key,
    required this.assetPath,
    required this.heroLabel,
    required this.heroTitle,
    required this.heroDrama,
    required this.version,
    required this.effectiveDate,
  });

  @override
  State<CinematicLegalPage> createState() => _CinematicLegalPageState();
}

class _CinematicLegalPageState extends State<CinematicLegalPage> {
  final ScrollController _scrollController = ScrollController();
  final ValueNotifier<Offset> _globalMousePos = ValueNotifier(Offset.zero);
  String? _markdown;
  String? _error;

  @override
  void initState() {
    super.initState();
    _loadMarkdown();
  }

  Future<void> _loadMarkdown() async {
    try {
      final content = await rootBundle.loadString(widget.assetPath);
      if (mounted) setState(() => _markdown = _stripFrontMatter(content));
    } catch (e) {
      if (mounted) {
        setState(() => _error = 'Legal content not found. Run: scripts/sync-legal.sh');
      }
    }
  }

  /// Remove the YAML-like front matter (title, dates) from the markdown
  /// since we render our own cinematic hero header.
  String _stripFrontMatter(String md) {
    final lines = md.split('\n');
    int startIndex = 0;

    // Skip the first H1 header line and any metadata lines (bold lines with **)
    for (int i = 0; i < lines.length; i++) {
      final trimmed = lines[i].trim();
      if (trimmed.startsWith('# ')) {
        startIndex = i + 1;
        continue;
      }
      if (trimmed.startsWith('**') && trimmed.endsWith('**')) {
        startIndex = i + 1;
        continue;
      }
      if (trimmed.isEmpty && startIndex > 0) {
        startIndex = i + 1;
        continue;
      }
      break;
    }

    return lines.sublist(startIndex).join('\n').trimLeft();
  }

  @override
  void dispose() {
    _scrollController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final isMobile = MediaQuery.of(context).size.width < 800;

    return Scaffold(
      body: Stack(
        children: [
          // Reactive canvas background
          Positioned.fill(
            child: InteractiveBackgroundLayer(mouseNotifier: _globalMousePos),
          ),
          // Noise texture
          const Positioned.fill(child: NoiseOverlay(opacity: 0.1)),
          // Main scroll content
          CustomScrollView(
            controller: _scrollController,
            slivers: [
              // Hero section
              SliverToBoxAdapter(child: _buildHero(theme, isMobile)),
              // Markdown content
              SliverPadding(
                padding: EdgeInsets.symmetric(
                  horizontal: isMobile ? 20 : 40,
                  vertical: 40,
                ),
                sliver: SliverToBoxAdapter(
                  child: Center(
                    child: ConstrainedBox(
                      constraints: const BoxConstraints(maxWidth: 900),
                      child: _buildContent(theme),
                    ),
                  ),
                ),
              ),
              const SliverToBoxAdapter(child: CinematicFooter()),
            ],
          ),
          // Floating back nav
          _buildBackNav(theme, isMobile),
          // Mouse tracker
          Positioned.fill(
            child: MouseRegion(
              hitTestBehavior: HitTestBehavior.translucent,
              onHover: (e) => _globalMousePos.value = e.localPosition,
              child: const SizedBox.expand(),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildHero(ThemeData theme, bool isMobile) {
    return Container(
      width: double.infinity,
      padding: EdgeInsets.only(
        top: isMobile ? 120 : 160,
        bottom: 60,
        left: isMobile ? 20 : 40,
        right: isMobile ? 20 : 40,
      ),
      child: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 900),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              // Label badge
              Container(
                padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 6),
                decoration: BoxDecoration(
                  color: theme.colorScheme.secondary.withValues(alpha: 0.1),
                  borderRadius: BorderRadius.circular(16),
                  border: Border.all(
                    color: theme.colorScheme.secondary.withValues(alpha: 0.3),
                  ),
                ),
                child: Text(
                  widget.heroLabel,
                  style: theme.textTheme.labelSmall,
                ),
              ),
              const SizedBox(height: 24),
              // Title
              Text(
                widget.heroTitle,
                style: theme.textTheme.displayLarge?.copyWith(
                  fontSize: isMobile ? 40 : 64,
                ),
              ),
              const SizedBox(height: 16),
              // Drama subtitle
              Text(
                widget.heroDrama,
                style: theme.textTheme.displayMedium?.copyWith(
                  fontSize: isMobile ? 28 : 48,
                ),
              ),
              const SizedBox(height: 32),
              // Version + date badges
              Wrap(
                spacing: 12,
                runSpacing: 8,
                children: [
                  _badge(theme, widget.version),
                  _badge(theme, widget.effectiveDate),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _badge(ThemeData theme, String text) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 6),
      decoration: BoxDecoration(
        color: theme.colorScheme.onSurface.withValues(alpha: 0.05),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(
          color: theme.colorScheme.onSurface.withValues(alpha: 0.1),
        ),
      ),
      child: Text(
        text,
        style: theme.textTheme.labelSmall?.copyWith(
          color: theme.colorScheme.onSurface.withValues(alpha: 0.7),
        ),
      ),
    );
  }

  Widget _buildContent(ThemeData theme) {
    if (_error != null) {
      return Container(
        padding: const EdgeInsets.all(24),
        decoration: BoxDecoration(
          color: theme.colorScheme.error.withValues(alpha: 0.1),
          borderRadius: BorderRadius.circular(16),
          border: Border.all(color: theme.colorScheme.error.withValues(alpha: 0.3)),
        ),
        child: Text(_error!, style: theme.textTheme.bodyLarge),
      );
    }

    if (_markdown == null) {
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(80),
          child: CircularProgressIndicator(
            color: theme.colorScheme.secondary,
          ),
        ),
      );
    }

    return MarkdownBody(
      data: _markdown!,
      selectable: true,
      styleSheet: _buildMarkdownStyle(theme),
    );
  }

  MarkdownStyleSheet _buildMarkdownStyle(ThemeData theme) {
    final accent = theme.colorScheme.secondary;
    final text = theme.colorScheme.onSurface;

    return MarkdownStyleSheet(
      // H2 — section headers
      h2: GoogleFonts.sora(
        fontSize: 28,
        fontWeight: FontWeight.w700,
        color: text,
        height: 1.3,
      ),
      h2Padding: const EdgeInsets.only(top: 48, bottom: 16),
      // H3 — subsection headers
      h3: GoogleFonts.sora(
        fontSize: 20,
        fontWeight: FontWeight.w600,
        color: text.withValues(alpha: 0.9),
        height: 1.4,
      ),
      h3Padding: const EdgeInsets.only(top: 32, bottom: 12),
      // Body text
      p: GoogleFonts.sora(
        fontSize: 16,
        color: text.withValues(alpha: 0.7),
        height: 1.7,
      ),
      pPadding: const EdgeInsets.only(bottom: 12),
      // Bold
      strong: GoogleFonts.sora(
        fontSize: 16,
        fontWeight: FontWeight.w600,
        color: text.withValues(alpha: 0.9),
      ),
      // Links
      a: GoogleFonts.sora(
        fontSize: 16,
        color: accent,
        decoration: TextDecoration.underline,
        decorationColor: accent.withValues(alpha: 0.5),
      ),
      // List items
      listBullet: GoogleFonts.sora(
        fontSize: 16,
        color: accent,
      ),
      listBulletPadding: const EdgeInsets.only(right: 8),
      listIndent: 24,
      // Blockquotes — highlight blocks
      blockquote: GoogleFonts.sora(
        fontSize: 15,
        color: text.withValues(alpha: 0.8),
        height: 1.6,
      ),
      blockquoteDecoration: BoxDecoration(
        color: accent.withValues(alpha: 0.05),
        border: Border(
          left: BorderSide(color: accent.withValues(alpha: 0.4), width: 3),
        ),
        borderRadius: const BorderRadius.only(
          topRight: Radius.circular(8),
          bottomRight: Radius.circular(8),
        ),
      ),
      blockquotePadding: const EdgeInsets.all(16),
      // Code blocks (for any legal references)
      code: GoogleFonts.firaCode(
        fontSize: 14,
        color: accent,
        backgroundColor: accent.withValues(alpha: 0.08),
      ),
      codeblockDecoration: BoxDecoration(
        color: accent.withValues(alpha: 0.05),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: accent.withValues(alpha: 0.1)),
      ),
      codeblockPadding: const EdgeInsets.all(16),
      // Horizontal rules
      horizontalRuleDecoration: BoxDecoration(
        border: Border(
          top: BorderSide(color: text.withValues(alpha: 0.1), width: 1),
        ),
      ),
      // Table styling
      tableHead: GoogleFonts.sora(
        fontSize: 14,
        fontWeight: FontWeight.w600,
        color: text.withValues(alpha: 0.9),
      ),
      tableBody: GoogleFonts.sora(
        fontSize: 14,
        color: text.withValues(alpha: 0.7),
      ),
      tableBorder: TableBorder.all(
        color: text.withValues(alpha: 0.1),
        width: 1,
        borderRadius: BorderRadius.circular(8),
      ),
      tableCellsPadding: const EdgeInsets.all(12),
    );
  }

  Widget _buildBackNav(ThemeData theme, bool isMobile) {
    return Positioned(
      top: 32,
      left: isMobile ? 16 : 32,
      child: GestureDetector(
        onTap: () => goToMarketingHome(context),
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
          decoration: BoxDecoration(
            color: theme.scaffoldBackgroundColor.withValues(alpha: 0.6),
            borderRadius: BorderRadius.circular(24),
            border: Border.all(
              color: theme.colorScheme.onSurface.withValues(alpha: 0.1),
            ),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(
                Icons.arrow_back_rounded,
                size: 18,
                color: theme.colorScheme.onSurface.withValues(alpha: 0.7),
              ),
              const SizedBox(width: 8),
              Text(
                AppLocalizations.of(context).uiBack,
                style: theme.textTheme.bodyMedium?.copyWith(
                  fontWeight: FontWeight.w500,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
