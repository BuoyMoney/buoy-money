/// Human-friendly label for a budget period, derived from its actual span
/// rather than just its start month.
///
/// The budget history picker used to label every period by "Month Year", so
/// several sub-monthly periods in the same month all read the same (e.g. three
/// "May 2026" chips for May 1–14, May 15–28, May 29–Jun 11). This names each
/// period by what it actually covers:
///
/// - Full calendar month   → "Jun 2026"
/// - Full calendar quarter  → "Q3 2026"
/// - Full calendar year     → "2026"
/// - Anything else          → a compact date range ("May 1–14, 2026",
///   "May 29 – Jun 11, 2026", "Dec 28, 2026 – Jan 3, 2027")
///
/// [end] is the period's inclusive last day. The `>= 28` day-of-month check
/// absorbs inclusive-last-day handling and short months (February).
String budgetPeriodLabel(DateTime start, DateTime end) {
  const months = [
    'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
    'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec',
  ];
  final endsMonth = end.day >= 28; // tolerant of 28/29/30/31 month ends
  final sameYear = start.year == end.year;

  // Full calendar year (Jan 1 → Dec 31)
  if (start.month == 1 &&
      start.day == 1 &&
      sameYear &&
      end.month == 12 &&
      endsMonth) {
    return '${start.year}';
  }

  // Full calendar quarter (Q1 Jan–Mar, Q2 Apr–Jun, Q3 Jul–Sep, Q4 Oct–Dec)
  if (start.day == 1 &&
      (start.month == 1 ||
          start.month == 4 ||
          start.month == 7 ||
          start.month == 10) &&
      sameYear &&
      end.month == start.month + 2 &&
      endsMonth) {
    return 'Q${(start.month - 1) ~/ 3 + 1} ${start.year}';
  }

  // Full calendar month (1st → last day of the same month)
  if (start.day == 1 && sameYear && end.month == start.month && endsMonth) {
    return '${months[start.month - 1]} ${start.year}';
  }

  // Otherwise: a compact date range.
  final startStr = '${months[start.month - 1]} ${start.day}';
  if (sameYear && start.month == end.month) {
    return '$startStr–${end.day}, ${start.year}'; // May 1–14, 2026
  }
  final endStr = '${months[end.month - 1]} ${end.day}';
  if (sameYear) {
    return '$startStr – $endStr, ${start.year}'; // May 29 – Jun 11, 2026
  }
  return '$startStr, ${start.year} – $endStr, ${end.year}'; // cross-year
}
