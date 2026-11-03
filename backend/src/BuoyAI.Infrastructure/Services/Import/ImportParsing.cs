using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BuoyAI.Core.DTOs.Import;

namespace BuoyAI.Infrastructure.Services.Import;

/// <summary>One account named by the file (an OFX statement, an Actual or YNAB account).</summary>
public sealed class ParsedImportAccount
{
  public string Key { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  /// <summary>Checking, Savings, CreditCard, Cash, Investment or Other.</summary>
  public string Type { get; set; } = "Checking";
  public bool Closed { get; set; }
}

/// <summary>One transaction as the file states it, before any matching against stored data.</summary>
public sealed class ParsedImportRow
{
  public int RowId { get; set; }
  public string AccountKey { get; set; } = string.Empty;
  public DateOnly? Date { get; set; }

  /// <summary>BuoyAI sign: money out is negative.</summary>
  public decimal Amount { get; set; }
  public string Payee { get; set; } = string.Empty;
  public string? Memo { get; set; }
  public string? OriginalDescription { get; set; }
  public string? CheckNumber { get; set; }
  public string? CategoryGroup { get; set; }
  public string? CategoryName { get; set; }

  /// <summary>The id the source gave the transaction (FITID, Actual id). Null when the source has none.</summary>
  public string? SourceId { get; set; }

  /// <summary>Counts identical rows (same account, date, amount, payee) earlier in the file, so repeats keep distinct ids.</summary>
  public int Occurrence { get; set; }

  /// <summary>The other account of a transfer, by the file's name for it.</summary>
  public string? TransferAccountName { get; set; }
  public bool IsStartingBalance { get; set; }

  /// <summary>A transfer whose other account is also in the file. Filed under the Transfer category so budgets ignore it.</summary>
  public bool IsTransfer { get; set; }
  public string? Error { get; set; }
}

public sealed class ParsedImportCategory
{
  public string Group { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public bool IsIncome { get; set; }
}

public sealed class ParsedBudgetEntry
{
  /// <summary>yyyy-MM</summary>
  public string Month { get; set; } = string.Empty;
  public string Group { get; set; } = string.Empty;
  public string Category { get; set; } = string.Empty;
  public decimal Budgeted { get; set; }
  public decimal? Available { get; set; }
}

public sealed class ParsedImport
{
  public ImportSource Source { get; set; }
  public List<ParsedImportAccount> Accounts { get; } = new();
  public List<ParsedImportCategory> Categories { get; } = new();
  public List<ParsedImportRow> Rows { get; } = new();
  public List<ParsedBudgetEntry> Budgets { get; } = new();
  public List<string> Warnings { get; } = new();
  public ImportDetectedInfo Detected { get; } = new();

  /// <summary>Day and month could not be told apart (and the user did not choose).</summary>
  public bool DateOrderAmbiguous { get; set; }
}

/// <summary>Reads text out of a file whose encoding the file does not always declare.</summary>
public static class ImportText
{
  static ImportText()
  {
    // Windows-1252 is not built in on .NET Core; the code pages provider adds it.
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
  }

  public static (string Text, string EncodingName) Decode(byte[] bytes, string? declaredCharset = null)
  {
    if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
      return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "utf-8");
    if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
      return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "utf-16le");
    if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
      return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "utf-16be");

    if (!string.IsNullOrWhiteSpace(declaredCharset))
    {
      var declared = declaredCharset.Trim();
      var encoding = declared switch
      {
        "1252" or "WINDOWS-1252" or "CP1252" => Encoding.GetEncoding(1252),
        "USASCII" or "ASCII" => Encoding.ASCII,
        "UTF-8" or "UTF8" => null,
        _ => null
      };
      if (encoding != null)
        return (encoding.GetString(bytes), encoding.WebName);
    }

    try
    {
      var strict = new UTF8Encoding(false, true);
      return (strict.GetString(bytes), "utf-8");
    }
    catch (DecoderFallbackException)
    {
      // Not valid UTF-8: bank exports from older systems are almost always Windows-1252.
      return (Encoding.GetEncoding(1252).GetString(bytes), "windows-1252");
    }
  }

  /// <summary>Lowercase, drop long digit runs and noise words so two bank spellings of one payee compare alike.</summary>
  public static string NormalizePayee(string? text)
  {
    if (string.IsNullOrWhiteSpace(text)) return string.Empty;
    var lower = text.ToLowerInvariant();
    lower = Regex.Replace(lower, @"\d{4,}", " ");
    lower = Regex.Replace(lower, @"[^\p{L}\p{N}\s]", " ");
    var tokens = lower
      .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
      .Where(t => !NoiseWords.Contains(t));
    return string.Join(' ', tokens);
  }

  private static readonly HashSet<string> NoiseWords = new()
  {
    "pos", "purchase", "debit", "card", "checkcard", "ach", "visa", "withdrawal", "payment", "online", "recurring", "the", "inc", "llc"
  };

  /// <summary>0 to 1: how alike two payees are. Empty on either side returns 0.</summary>
  public static double PayeeSimilarity(string? a, string? b)
  {
    var na = NormalizePayee(a);
    var nb = NormalizePayee(b);
    if (na.Length == 0 || nb.Length == 0) return 0;
    if (na == nb) return 1;
    if (na.Length >= 4 && nb.Length >= 4 && (na.Contains(nb) || nb.Contains(na))) return 0.9;

    var ba = Bigrams(na);
    var bb = Bigrams(nb);
    if (ba.Count == 0 || bb.Count == 0) return 0;
    var remaining = new List<string>(bb);
    var shared = 0;
    foreach (var gram in ba)
    {
      var idx = remaining.IndexOf(gram);
      if (idx < 0) continue;
      shared++;
      remaining.RemoveAt(idx);
    }
    return 2.0 * shared / (ba.Count + bb.Count);
  }

  private static List<string> Bigrams(string s)
  {
    var squeezed = s.Replace(" ", string.Empty);
    var grams = new List<string>();
    for (var i = 0; i < squeezed.Length - 1; i++) grams.Add(squeezed.Substring(i, 2));
    return grams;
  }
}

/// <summary>
/// Reads money text in an explicit format. Never guesses silently: a value that does not fit the
/// chosen format is rejected, not bent into a wrong number (the old reader turned 1.234,56 into 1.23456).
/// </summary>
public static class ImportNumberParser
{
  private static readonly Regex UsPattern = new(@"^\d{1,3}(,\d{3})+(\.\d+)?$|^\d*\.?\d+$|^\d+\.$", RegexOptions.Compiled);
  private static readonly Regex EuPattern = new(@"^\d{1,3}([. ]\d{3})+(,\d+)?$|^\d*,?\d+$|^\d+,$", RegexOptions.Compiled);

  public static bool TryParse(string? text, ImportNumberFormat format, out decimal value)
  {
    value = 0;
    if (string.IsNullOrWhiteSpace(text)) return false;

    var s = text.Trim().Replace(' ', ' ').Replace("−", "-");
    var negative = false;

    // Trailing CR (credit, money in) or DR (debit, money out).
    var upper = s.ToUpperInvariant();
    if (upper.EndsWith("DR")) { negative = true; s = s[..^2].Trim(); }
    else if (upper.EndsWith("CR")) { s = s[..^2].Trim(); }

    if (s.StartsWith('(') && s.EndsWith(')'))
    {
      negative = !negative;
      s = s[1..^1].Trim();
    }

    // Currency symbols and codes.
    s = Regex.Replace(s, @"[$€£¥]|USD|EUR|GBP|CAD|AUD", string.Empty, RegexOptions.IgnoreCase).Trim();

    if (s.EndsWith('-')) { negative = !negative; s = s[..^1].Trim(); }
    if (s.StartsWith('-')) { negative = !negative; s = s[1..].Trim(); }
    else if (s.StartsWith('+')) { s = s[1..].Trim(); }
    if (s.Length == 0) return false;

    var resolved = format == ImportNumberFormat.Auto ? DetectOne(s) : format;
    string normalized;
    if (resolved == ImportNumberFormat.European)
    {
      if (!EuPattern.IsMatch(s)) return false;
      normalized = s.Replace(".", string.Empty).Replace(" ", string.Empty).Replace(',', '.');
    }
    else
    {
      if (!UsPattern.IsMatch(s)) return false;
      normalized = s.Replace(",", string.Empty);
    }

    if (!decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
      return false;
    if (negative) value = -value;
    return true;
  }

  /// <summary>Decides the format from one value when it is clear, otherwise US.</summary>
  private static ImportNumberFormat DetectOne(string s)
  {
    var lastComma = s.LastIndexOf(',');
    var lastDot = s.LastIndexOf('.');
    if (lastComma >= 0 && lastDot >= 0) return lastComma > lastDot ? ImportNumberFormat.European : ImportNumberFormat.Us;
    if (s.Contains(' ')) return ImportNumberFormat.European;
    if (lastComma >= 0 && s.Count(c => c == ',') == 1 && s.Length - lastComma - 1 is 1 or 2) return ImportNumberFormat.European;
    if (lastDot >= 0 && s.Count(c => c == '.') > 1) return ImportNumberFormat.European;
    return ImportNumberFormat.Us;
  }

  /// <summary>Picks one format for a whole column from sample values. Ambiguous when nothing in the sample shows it.</summary>
  public static ImportNumberFormat Detect(IEnumerable<string?> samples)
  {
    var us = 0;
    var eu = 0;
    foreach (var raw in samples)
    {
      if (string.IsNullOrWhiteSpace(raw)) continue;
      var s = Regex.Replace(raw.Trim(), @"[$€£¥()\-+]|USD|EUR|GBP|CAD|AUD|CR|DR", string.Empty, RegexOptions.IgnoreCase).Trim();
      var lastComma = s.LastIndexOf(',');
      var lastDot = s.LastIndexOf('.');
      if (lastComma >= 0 && lastDot >= 0)
      {
        if (lastComma > lastDot) eu++; else us++;
      }
      else if (lastComma >= 0)
      {
        // 12,5 or 12,50 reads as a decimal comma; 1,234 is ambiguous and counts for neither.
        if (s.Length - lastComma - 1 is 1 or 2 && s.Count(c => c == ',') == 1) eu++;
        else if (s.Count(c => c == ',') > 1) us++;
      }
      else if (lastDot >= 0)
      {
        if (s.Length - lastDot - 1 is 1 or 2 && s.Count(c => c == '.') == 1) us++;
        else if (s.Count(c => c == '.') > 1) eu++;
      }
      else if (s.Contains(' ') && Regex.IsMatch(s, @"^\d{1,3}( \d{3})+$")) eu++;
    }
    return eu > us ? ImportNumberFormat.European : ImportNumberFormat.Us;
  }
}

/// <summary>Reads text dates, with the day/month order stated or detected, never assumed silently.</summary>
public static class ImportDateParser
{
  private static readonly Regex YearFirst = new(@"^(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})$", RegexOptions.Compiled);
  private static readonly Regex Compact = new(@"^(\d{4})(\d{2})(\d{2})$", RegexOptions.Compiled);
  private static readonly Regex Triple = new(@"^(\d{1,2})[-/.](\d{1,2})[-/.](\d{2}|\d{4})$", RegexOptions.Compiled);
  private static readonly string[] NamedFormats =
  {
    "MMM d, yyyy", "MMM d yyyy", "MMMM d, yyyy", "MMMM d yyyy", "d MMM yyyy", "d MMMM yyyy",
    "d-MMM-yyyy", "dd-MMM-yy", "d-MMM-yy", "dd-MMM-yyyy", "MMM-dd-yyyy"
  };

  public static bool TryParse(string? text, ImportDateOrder order, out DateOnly date)
  {
    date = default;
    if (string.IsNullOrWhiteSpace(text)) return false;
    var s = StripTime(text.Trim());

    var m = Compact.Match(s);
    if (m.Success) return Build(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), out date);

    m = YearFirst.Match(s);
    if (m.Success) return Build(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), out date);

    m = Triple.Match(s);
    if (m.Success)
    {
      var a = int.Parse(m.Groups[1].Value);
      var b = int.Parse(m.Groups[2].Value);
      var y = ExpandYear(m.Groups[3].Value);
      return order == ImportDateOrder.DayFirst ? Build(y, b, a, out date) : Build(y, a, b, out date);
    }

    if (DateTime.TryParseExact(s, NamedFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var named))
    {
      date = DateOnly.FromDateTime(named);
      return true;
    }
    return false;
  }

  private static string StripTime(string s)
  {
    var t = s.IndexOf('T');
    if (t > 7 && char.IsDigit(s[t - 1])) return s[..t];
    var space = s.IndexOf(' ');
    if (space > 5 && s.IndexOf(':') > space) return s[..space];
    return s;
  }

  private static int ExpandYear(string y) =>
    y.Length == 4 ? int.Parse(y) : int.Parse(y) is var n && n < 70 ? 2000 + n : 1900 + n;

  private static bool Build(int y, int mo, int d, out DateOnly date)
  {
    date = default;
    if (y < 1900 || y > 2200 || mo < 1 || mo > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo)) return false;
    date = new DateOnly(y, mo, d);
    return true;
  }

  /// <summary>
  /// Decides the order from sample values. Returns the order, and whether it was a guess
  /// (every sample has both numbers at 12 or below).
  /// </summary>
  public static (ImportDateOrder Order, bool Ambiguous) Detect(IEnumerable<string?> samples)
  {
    var dayFirst = false;
    var monthFirst = false;
    var yearFirst = false;
    var sawTriple = false;
    foreach (var raw in samples)
    {
      if (string.IsNullOrWhiteSpace(raw)) continue;
      var s = StripTime(raw.Trim());
      if (YearFirst.IsMatch(s) || Compact.IsMatch(s)) { yearFirst = true; continue; }
      var m = Triple.Match(s);
      if (!m.Success) continue;
      sawTriple = true;
      var a = int.Parse(m.Groups[1].Value);
      var b = int.Parse(m.Groups[2].Value);
      if (a > 12) dayFirst = true;
      if (b > 12) monthFirst = true;
    }
    if (!sawTriple) return (yearFirst ? ImportDateOrder.YearFirst : ImportDateOrder.MonthFirst, false);
    if (dayFirst && !monthFirst) return (ImportDateOrder.DayFirst, false);
    if (monthFirst && !dayFirst) return (ImportDateOrder.MonthFirst, false);
    // Neither, or contradictory evidence: guess US order and tell the user.
    return (ImportDateOrder.MonthFirst, !dayFirst && !monthFirst);
  }
}
