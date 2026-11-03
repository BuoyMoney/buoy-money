import '../../l10n/generated/app_localizations.dart';
import 'package:fl_chart/fl_chart.dart';
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:intl/intl.dart';

import '../../services/visualization_service.dart';

/// #388 Phase 1 — renders a structured [UserVisualization] (the existing
/// VisualizationSpec / ChartDataPoint pipeline) natively with fl_chart, styled to
/// the cinematic theme. Supports bar / line / area / pie / donut; unknown types
/// fall back to a bar chart.
///
/// Interactive: bars and lines carry touch tooltips (name + full value), axis
/// labels compact large values ($1.2K) so they never overflow the reserved
/// gutter, and donuts label their total in the center hole.
class VisualizationChart extends StatelessWidget {
  final UserVisualization viz;
  final double height;

  /// When true, render only the chart (+ legend) — for surfaces that already show
  /// the title/message (e.g. the insights card).
  final bool bare;

  const VisualizationChart({
    super.key,
    required this.viz,
    this.height = 220,
    this.bare = false,
  });

  static const _accent = Color(0xFF7B61FF);
  static const _teal = Color(0xFF4ECDC4);
  static const _textGhost = Color(0xFFF0EFF4);
  static const _textMuted = Color(0xFF8A8A9A);
  static const _grid = Color(0x14FFFFFF);
  static const _tooltipBg = Color(0xFF1A1A2E);

  // A small on-brand palette used when a data point has no explicit color.
  static const _palette = [
    Color(0xFF7B61FF),
    Color(0xFF4ECDC4),
    Color(0xFFFF6B6B),
    Color(0xFFFFD93D),
    Color(0xFF6BCB77),
    Color(0xFF4D96FF),
    Color(0xFFFF9F45),
  ];

  Color _colorFor(int i, ChartDataPoint p) {
    final c = p.color;
    if (c != null && c.isNotEmpty) {
      final hex = c.replaceFirst('#', '');
      final v = int.tryParse(hex.length == 6 ? 'FF$hex' : hex, radix: 16);
      if (v != null) return Color(v);
    }
    return _palette[i % _palette.length];
  }

  String _fmt(double v) {
    final cfg = viz.chartConfig;
    final prefix = cfg?.valuePrefix ?? '';
    final suffix = cfg?.valueSuffix ?? '';
    final s = v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toStringAsFixed(1);
    return '$prefix$s$suffix';
  }

  /// Axis-gutter variant: compacts ≥1000 to $1.2K so left labels never
  /// overflow their reserved width.
  String _fmtAxis(double v) {
    final cfg = viz.chartConfig;
    final prefix = cfg?.valuePrefix ?? '';
    final suffix = cfg?.valueSuffix ?? '';
    if (v.abs() >= 1000) {
      return '$prefix${NumberFormat.compact().format(v)}$suffix';
    }
    return '$prefix${v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toStringAsFixed(1)}$suffix';
  }

  double get _maxValue => viz.chartData.fold<double>(
      0, (m, p) => p.value > m ? p.value : m);

  @override
  Widget build(BuildContext context) {
    final data = viz.chartData;
    if (data.isEmpty) return const SizedBox.shrink();

    final type = viz.chartType.toLowerCase();
    final Widget chart = switch (type) {
      'pie' || 'donut' => _pie(context, donut: type == 'donut'),
      'line' || 'area' => _line(area: type == 'area'),
      _ => _bar(),
    };

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (!bare && viz.title.isNotEmpty) ...[
          Text(viz.title,
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
              style: GoogleFonts.sora(
                  fontSize: 14, fontWeight: FontWeight.w600, color: _textGhost)),
          const SizedBox(height: 12),
        ],
        SizedBox(height: height, child: chart),
        if ((viz.chartConfig?.showLegend ?? true) &&
            (type == 'pie' || type == 'donut'))
          _legend(data),
        if (!bare && viz.message.isNotEmpty) ...[
          const SizedBox(height: 8),
          Text(viz.message,
              style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
        ],
      ],
    );
  }

  Widget _bar() {
    final data = viz.chartData;
    return BarChart(
      BarChartData(
        alignment: BarChartAlignment.spaceAround,
        // Headroom so the tallest bar's tooltip never clips at the top edge.
        maxY: _maxValue * 1.18,
        borderData: FlBorderData(show: false),
        gridData: FlGridData(
          show: true,
          drawVerticalLine: false,
          getDrawingHorizontalLine: (_) => const FlLine(color: _grid, strokeWidth: 1),
        ),
        titlesData: _titles(data),
        barTouchData: BarTouchData(
          touchTooltipData: BarTouchTooltipData(
            getTooltipColor: (_) => _tooltipBg,
            tooltipBorderRadius: BorderRadius.circular(8),
            getTooltipItem: (group, _, rod, __) => BarTooltipItem(
              '${data[group.x].name}\n',
              GoogleFonts.sora(fontSize: 11, color: _textMuted),
              children: [
                TextSpan(
                  text: _fmt(rod.toY),
                  style: GoogleFonts.firaCode(
                      fontSize: 13,
                      fontWeight: FontWeight.w700,
                      color: _textGhost),
                ),
              ],
            ),
          ),
        ),
        barGroups: [
          for (var i = 0; i < data.length; i++)
            BarChartGroupData(x: i, barRods: [
              BarChartRodData(
                toY: data[i].value,
                gradient: LinearGradient(
                  begin: Alignment.bottomCenter,
                  end: Alignment.topCenter,
                  colors: [
                    _colorFor(i, data[i]).withValues(alpha: 0.45),
                    _colorFor(i, data[i]),
                  ],
                ),
                width: 18,
                borderRadius: const BorderRadius.vertical(top: Radius.circular(6)),
              ),
            ]),
        ],
      ),
    );
  }

  Widget _line({required bool area}) {
    final data = viz.chartData;
    return LineChart(
      LineChartData(
        maxY: _maxValue * 1.15,
        borderData: FlBorderData(show: false),
        gridData: FlGridData(
          show: true,
          drawVerticalLine: false,
          getDrawingHorizontalLine: (_) => const FlLine(color: _grid, strokeWidth: 1),
        ),
        titlesData: _titles(data),
        lineTouchData: LineTouchData(
          touchTooltipData: LineTouchTooltipData(
            getTooltipColor: (_) => _tooltipBg,
            tooltipBorderRadius: BorderRadius.circular(8),
            getTooltipItems: (spots) => spots
                .map((s) => LineTooltipItem(
                      '${data[s.x.toInt().clamp(0, data.length - 1)].name}\n',
                      GoogleFonts.sora(fontSize: 11, color: _textMuted),
                      children: [
                        TextSpan(
                          text: _fmt(s.y),
                          style: GoogleFonts.firaCode(
                              fontSize: 13,
                              fontWeight: FontWeight.w700,
                              color: _textGhost),
                        ),
                      ],
                    ))
                .toList(),
          ),
        ),
        lineBarsData: [
          LineChartBarData(
            spots: [for (var i = 0; i < data.length; i++) FlSpot(i.toDouble(), data[i].value)],
            isCurved: true,
            gradient: const LinearGradient(colors: [_accent, _teal]),
            barWidth: 3,
            // Mark only the latest point — a quiet "you are here".
            dotData: FlDotData(
              show: true,
              checkToShowDot: (spot, _) => spot.x == (data.length - 1).toDouble(),
              getDotPainter: (_, __, ___, ____) => FlDotCirclePainter(
                radius: 4,
                color: _teal,
                strokeWidth: 2,
                strokeColor: _textGhost,
              ),
            ),
            belowBarData: BarAreaData(
              show: area,
              gradient: LinearGradient(
                begin: Alignment.topCenter,
                end: Alignment.bottomCenter,
                colors: [
                  _accent.withValues(alpha: 0.25),
                  _accent.withValues(alpha: 0.0),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }

  Widget _pie(BuildContext context, {required bool donut}) {
    final data = viz.chartData;
    final total = data.fold<double>(0, (s, p) => s + p.value);
    final chart = PieChart(
      PieChartData(
        sectionsSpace: 2,
        centerSpaceRadius: donut ? 44 : 0,
        sections: [
          for (var i = 0; i < data.length; i++)
            PieChartSectionData(
              value: data[i].value,
              color: _colorFor(i, data[i]),
              // Only label comfortably-large slices on the chart; every slice's value
              // is shown in the legend below, so small adjacent slices never overlap.
              title: (total > 0 && data[i].value / total < 0.12)
                  ? ''
                  : _fmt(data[i].value),
              titlePositionPercentageOffset: 0.6,
              radius: donut ? 48 : 72,
              titleStyle: GoogleFonts.firaCode(
                  fontSize: 11, fontWeight: FontWeight.w600, color: Colors.white),
            ),
        ],
      ),
    );
    if (!donut) return chart;
    // The hole is prime real estate — label the total there.
    return Stack(
      alignment: Alignment.center,
      children: [
        chart,
        Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(_fmtAxis(total),
                style: GoogleFonts.firaCode(
                    fontSize: 16,
                    fontWeight: FontWeight.w700,
                    color: _textGhost)),
            Text(AppLocalizations.of(context).uiChartTotal,
                style: GoogleFonts.sora(fontSize: 10, color: _textMuted)),
          ],
        ),
      ],
    );
  }

  Widget _legend(List<ChartDataPoint> data) {
    return Padding(
      padding: const EdgeInsets.only(top: 12),
      child: Wrap(
        spacing: 12,
        runSpacing: 6,
        children: [
          for (var i = 0; i < data.length; i++)
            Row(mainAxisSize: MainAxisSize.min, children: [
              Container(
                width: 10,
                height: 10,
                decoration: BoxDecoration(
                    color: _colorFor(i, data[i]), shape: BoxShape.circle),
              ),
              const SizedBox(width: 6),
              Text('${data[i].name}  ',
                  style: GoogleFonts.sora(fontSize: 11, color: _textMuted)),
              // Carry the value in the legend so hidden small-slice labels lose no info.
              Text(_fmt(data[i].value),
                  style: GoogleFonts.firaCode(
                      fontSize: 11,
                      fontWeight: FontWeight.w600,
                      color: _textGhost)),
            ]),
        ],
      ),
    );
  }

  FlTitlesData _titles(List<ChartDataPoint> data) {
    // Thin out bottom labels on dense charts instead of ellipsizing them all.
    final labelInterval = (data.length / 6).ceil().clamp(1, 100).toDouble();
    return FlTitlesData(
      topTitles: const AxisTitles(sideTitles: SideTitles(showTitles: false)),
      rightTitles: const AxisTitles(sideTitles: SideTitles(showTitles: false)),
      leftTitles: AxisTitles(
        sideTitles: SideTitles(
          showTitles: true,
          reservedSize: 44,
          getTitlesWidget: (value, meta) => Text(
            _fmtAxis(value),
            style: GoogleFonts.firaCode(fontSize: 9, color: _textMuted),
          ),
        ),
      ),
      bottomTitles: AxisTitles(
        sideTitles: SideTitles(
          showTitles: true,
          reservedSize: 28,
          // Only one label per data point — without interval fl_chart samples the
          // line axis at fractional ticks that round to the same index (repeated labels).
          interval: labelInterval,
          getTitlesWidget: (value, meta) {
            if (value != value.roundToDouble()) return const SizedBox.shrink();
            final i = value.toInt();
            if (i < 0 || i >= data.length) return const SizedBox.shrink();
            return Padding(
              padding: const EdgeInsets.only(top: 6),
              child: Text(
                data[i].name,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: GoogleFonts.sora(fontSize: 9, color: _textMuted),
              ),
            );
          },
        ),
      ),
    );
  }
}
