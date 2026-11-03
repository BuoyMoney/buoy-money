// ignore_for_file: use_key_in_widget_constructors
import 'dart:ui';
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import '../../l10n/generated/app_localizations.dart';
import '../../utils/currency.dart';
import '../../services/categories_service.dart';
import '../../utils/formatters.dart';

// VaporClinic palette
const sheetAccent = Color(0xFF7B61FF);
const sheetTextGhost = Color(0xFFF0EFF4);
const sheetTextMuted = Color(0xFF8A8A9A);
const sheetCardBg = Color(0x0DFFFFFF);
const sheetCardBorder = Color(0x14FFFFFF);
const sheetIncome = Color(0xFF34D399);
const sheetExpense = Color(0xFFF87171);
const sheetPending = Color(0xFFFBBF24);
const sheetBg = Color(0xFF0C0C18);

const sheetColorPalette = [
  '#3B82F6', '#8B5CF6', '#EC4899', '#EF4444',
  '#F97316', '#EAB308', '#22C55E', '#14B8A6',
  '#06B6D4', '#6366F1', '#A855F7', '#F43F5E',
  '#10B981', '#0EA5E9', '#84CC16', '#F59E0B',
];

const sheetIconPalette = [
  '🏠', '🚗', '🛒', '🍽️',
  '💊', '🎓', '✈️', '🎁',
  '💰', '📱', '👕', '🎮',
  '💼', '⚡', '🏋️', '📋',
];

//  SHARED WIDGETS

class SheetDragHandle extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(top: 12, bottom: 8),
      child: Container(
        width: 40,
        height: 4,
        decoration: BoxDecoration(
          color: sheetTextMuted.withValues(alpha: 0.3),
          borderRadius: BorderRadius.circular(2),
        ),
      ),
    );
  }
}

class SheetGlassCard extends StatelessWidget {
  final Widget child;
  const SheetGlassCard({required this.child});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: sheetCardBg,
        borderRadius: BorderRadius.circular(16),
        border: Border.all(color: sheetCardBorder),
      ),
      child: child,
    );
  }
}

class SheetTextField extends StatelessWidget {
  final TextEditingController? controller;
  final String? label;
  final String? hint;
  final String? prefix;
  final TextInputType? keyboardType;
  final ValueChanged<String>? onChanged;

  const SheetTextField({
    this.controller,
    this.label,
    this.hint,
    this.prefix,
    this.keyboardType,
    this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (label != null)
          Padding(
            padding: const EdgeInsets.only(bottom: 6),
            child: Text(label!, style: GoogleFonts.sora(fontSize: 12, color: sheetTextMuted)),
          ),
        TextFormField(
          controller: controller,
          keyboardType: keyboardType,
          onChanged: onChanged,
          style: GoogleFonts.sora(fontSize: 15, color: sheetTextGhost),
          decoration: InputDecoration(
            hintText: hint,
            hintStyle: GoogleFonts.sora(fontSize: 14, color: sheetTextMuted.withValues(alpha: 0.5)),
            prefixText: prefix,
            prefixStyle: GoogleFonts.firaCode(fontSize: 15, color: sheetTextMuted),
            filled: true,
            fillColor: sheetCardBg,
            border: OutlineInputBorder(
              borderRadius: BorderRadius.circular(12),
              borderSide: BorderSide(color: sheetCardBorder),
            ),
            enabledBorder: OutlineInputBorder(
              borderRadius: BorderRadius.circular(12),
              borderSide: BorderSide(color: sheetCardBorder),
            ),
            focusedBorder: OutlineInputBorder(
              borderRadius: BorderRadius.circular(12),
              borderSide: const BorderSide(color: sheetAccent),
            ),
            contentPadding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
          ),
        ),
      ],
    );
  }
}

class SheetDropdown<T> extends StatelessWidget {
  final T? value;
  final String hint;
  final List<DropdownMenuItem<T>> items;
  final ValueChanged<T?>? onChanged;

  const SheetDropdown({
    this.value,
    required this.hint,
    required this.items,
    this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 16),
      decoration: BoxDecoration(
        color: sheetCardBg,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: sheetCardBorder),
      ),
      child: DropdownButtonHideUnderline(
        child: DropdownButton<T>(
          value: value,
          hint: Text(hint, style: GoogleFonts.sora(fontSize: 14, color: sheetTextMuted)),
          isExpanded: true,
          dropdownColor: sheetBg,
          items: items,
          onChanged: onChanged,
        ),
      ),
    );
  }
}

class SheetAccentButton extends StatelessWidget {
  final String label;
  final VoidCallback? onPressed;

  const SheetAccentButton({required this.label, this.onPressed});

  @override
  Widget build(BuildContext context) {
    return ElevatedButton(
      onPressed: onPressed,
      style: ElevatedButton.styleFrom(
        backgroundColor: sheetAccent,
        foregroundColor: sheetTextGhost,
        disabledBackgroundColor: sheetAccent.withValues(alpha: 0.3),
        disabledForegroundColor: sheetTextGhost.withValues(alpha: 0.3),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
      ),
      child: Text(label, style: GoogleFonts.sora(fontWeight: FontWeight.w600)),
    );
  }
}

class SheetChoiceChip extends StatelessWidget {
  final String label;
  final bool selected;
  final VoidCallback onSelected;

  const SheetChoiceChip({
    required this.label,
    required this.selected,
    required this.onSelected,
  });

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onSelected,
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 200),
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
        decoration: BoxDecoration(
          color: selected ? sheetAccent.withValues(alpha: 0.2) : sheetCardBg,
          borderRadius: BorderRadius.circular(20),
          border: Border.all(color: selected ? sheetAccent : sheetCardBorder),
        ),
        child: Text(label,
            style: GoogleFonts.sora(
                fontSize: 13,
                color: selected ? sheetAccent : sheetTextMuted,
                fontWeight: selected ? FontWeight.w600 : FontWeight.normal)),
      ),
    );
  }
}

class SheetToggleRow extends StatelessWidget {
  final String label;
  final String? subtitle;
  final bool value;
  final ValueChanged<bool> onChanged;

  const SheetToggleRow({
    required this.label,
    this.subtitle,
    required this.value,
    required this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(label, style: GoogleFonts.sora(fontSize: 14, color: sheetTextGhost)),
              if (subtitle != null)
                Text(subtitle!, style: GoogleFonts.sora(fontSize: 11, color: sheetTextMuted)),
            ],
          ),
        ),
        Switch.adaptive(
          value: value,
          onChanged: onChanged,
          activeTrackColor: sheetAccent,
          inactiveTrackColor: sheetCardBg,
        ),
      ],
    );
  }
}

class SheetRadioOption<T> extends StatelessWidget {
  final String label;
  final String? subtitle;
  final T value;
  final T groupValue;
  final ValueChanged<T?> onChanged;
  final Color? color;

  const SheetRadioOption({
    required this.label,
    this.subtitle,
    required this.value,
    required this.groupValue,
    required this.onChanged,
    this.color,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 4),
      child: InkWell(
        borderRadius: BorderRadius.circular(8),
        onTap: () => onChanged(value),
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: 6),
          child: Row(
            children: [
              // ignore: deprecated_member_use
              Radio<T>.adaptive(
                value: value,
                // ignore: deprecated_member_use
                groupValue: groupValue,
                // ignore: deprecated_member_use
                onChanged: onChanged,
                fillColor: WidgetStateProperty.resolveWith((states) {
                  if (states.contains(WidgetState.selected)) return color ?? sheetAccent;
                  return sheetTextMuted;
                }),
              ),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(label,
                        style: GoogleFonts.sora(fontSize: 14, color: color ?? sheetTextGhost)),
                    if (subtitle != null)
                      Text(subtitle!,
                          style: GoogleFonts.sora(fontSize: 11, color: sheetTextMuted)),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class SheetSummaryItem extends StatelessWidget {
  final String label;
  final String value;
  final Color color;

  const SheetSummaryItem(this.label, this.value, this.color);

  @override
  Widget build(BuildContext context) {
    return Column(
      children: [
        Text(label, style: GoogleFonts.sora(fontSize: 11, color: sheetTextMuted)),
        const SizedBox(height: 4),
        Text(value,
            style: GoogleFonts.firaCode(fontSize: 14, color: color, fontWeight: FontWeight.w600)),
      ],
    );
  }
}

class SheetCategoryAllocationRow extends StatelessWidget {
  final Category category;
  final double? amount;
  final bool rollover;
  final bool countUp;
  final bool hideToggles;
  final ValueChanged<double?> onAmountChanged;
  final ValueChanged<bool> onRolloverChanged;
  final ValueChanged<bool> onCountUpChanged;

  const SheetCategoryAllocationRow({
    required this.category,
    this.amount,
    required this.rollover,
    required this.countUp,
    this.hideToggles = false,
    required this.onAmountChanged,
    required this.onRolloverChanged,
    required this.onCountUpChanged,
  });

  @override
  Widget build(BuildContext context) {
    final hasAmount = amount != null && amount! > 0;
    return Padding(
      padding: const EdgeInsets.only(bottom: 6),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
        decoration: BoxDecoration(
          color: hasAmount ? sheetAccent.withValues(alpha: 0.06) : sheetCardBg,
          borderRadius: BorderRadius.circular(10),
          border: Border.all(color: hasAmount ? sheetAccent.withValues(alpha: 0.2) : sheetCardBorder),
        ),
        child: Column(
          children: [
            Row(
              children: [
                Expanded(
                  flex: 3,
                  child: Text(category.name,
                      style: GoogleFonts.sora(
                          fontSize: 13,
                          color: hasAmount ? sheetTextGhost : sheetTextMuted,
                          fontWeight: hasAmount ? FontWeight.w500 : FontWeight.normal)),
                ),
                const SizedBox(width: 8),
                SizedBox(
                  width: 100,
                  child: TextFormField(
                    initialValue: amount?.toStringAsFixed(0) ?? '',
                    keyboardType: const TextInputType.numberWithOptions(decimal: true),
                    style: GoogleFonts.firaCode(fontSize: 13, color: sheetTextGhost),
                    onChanged: (v) => onAmountChanged(parseFlexibleAmount(v)),
                    decoration: InputDecoration(
                      hintText: '${primaryCurrencySymbol}0',
                      hintStyle: GoogleFonts.firaCode(fontSize: 13, color: sheetTextMuted.withValues(alpha: 0.3)),
                      isDense: true,
                      contentPadding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
                      border: OutlineInputBorder(
                        borderRadius: BorderRadius.circular(6),
                        borderSide: BorderSide(color: sheetCardBorder),
                      ),
                      enabledBorder: OutlineInputBorder(
                        borderRadius: BorderRadius.circular(6),
                        borderSide: BorderSide(color: sheetCardBorder),
                      ),
                      focusedBorder: OutlineInputBorder(
                        borderRadius: BorderRadius.circular(6),
                        borderSide: const BorderSide(color: sheetAccent),
                      ),
                    ),
                  ),
                ),
              ],
            ),
            if (hasAmount && !hideToggles)
              Padding(
                padding: const EdgeInsets.only(top: 4),
                child: Row(
                  children: [
                    SheetMiniToggle(
                      icon: Icons.repeat,
                      label: 'Rollover',
                      value: rollover,
                      onChanged: onRolloverChanged,
                    ),
                    const SizedBox(width: 12),
                    SheetMiniToggle(
                      icon: Icons.trending_up,
                      label: 'Count-Up',
                      value: countUp,
                      onChanged: onCountUpChanged,
                    ),
                  ],
                ),
              ),
          ],
        ),
      ),
    );
  }
}

class SheetMiniToggle extends StatelessWidget {
  final IconData icon;
  final String label;
  final bool value;
  final ValueChanged<bool> onChanged;

  const SheetMiniToggle({
    required this.icon,
    required this.label,
    required this.value,
    required this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: () => onChanged(!value),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 14, color: value ? sheetAccent : sheetTextMuted.withValues(alpha: 0.4)),
          const SizedBox(width: 4),
          Text(label,
              style: GoogleFonts.sora(
                  fontSize: 10,
                  color: value ? sheetAccent : sheetTextMuted.withValues(alpha: 0.4))),
        ],
      ),
    );
  }
}

/// Confirmation dialog in glassmorphic style
Future<bool?> showBudgetConfirmDialog(
  BuildContext context, {
  required String title,
  required String message,
  String confirmLabel = 'Confirm',
  Color confirmColor = sheetExpense,
}) {
  return showDialog<bool>(
    context: context,
    builder: (ctx) => Dialog(
      backgroundColor: Colors.transparent,
      child: ClipRRect(
        borderRadius: BorderRadius.circular(20),
        child: BackdropFilter(
          filter: ImageFilter.blur(sigmaX: 16, sigmaY: 16),
          child: Container(
            padding: const EdgeInsets.all(24),
            decoration: BoxDecoration(
              color: sheetBg.withValues(alpha: 0.95),
              borderRadius: BorderRadius.circular(20),
              border: Border.all(color: sheetCardBorder),
            ),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Text(title,
                    style: GoogleFonts.sora(
                        fontSize: 18, fontWeight: FontWeight.bold, color: sheetTextGhost)),
                const SizedBox(height: 12),
                Text(message,
                    style: GoogleFonts.sora(fontSize: 14, color: sheetTextMuted),
                    textAlign: TextAlign.center),
                const SizedBox(height: 24),
                Row(
                  children: [
                    Expanded(
                      child: OutlinedButton(
                        onPressed: () => Navigator.of(ctx).pop(false),
                        style: OutlinedButton.styleFrom(
                          foregroundColor: sheetTextMuted,
                          side: BorderSide(color: sheetCardBorder),
                          shape: RoundedRectangleBorder(
                            borderRadius: BorderRadius.circular(10)),
                        ),
                        child: Text(AppLocalizations.of(ctx).budCancel, style: GoogleFonts.sora()),
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: ElevatedButton(
                        onPressed: () => Navigator.of(ctx).pop(true),
                        style: ElevatedButton.styleFrom(
                          backgroundColor: confirmColor.withValues(alpha: 0.2),
                          foregroundColor: confirmColor,
                          shape: RoundedRectangleBorder(
                            borderRadius: BorderRadius.circular(10),
                            side: BorderSide(color: confirmColor.withValues(alpha: 0.4)),
                          ),
                        ),
                        child: Text(confirmLabel, style: GoogleFonts.sora(fontWeight: FontWeight.w600)),
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
        ),
      ),
    ),
  );
}

Color parseCategoryColor(String? hex) {
  if (hex == null || hex.isEmpty) return sheetAccent;
  try {
    final clean = hex.replaceFirst('#', '');
    return Color(int.parse('FF$clean', radix: 16));
  } catch (_) {
    return sheetAccent;
  }
}
