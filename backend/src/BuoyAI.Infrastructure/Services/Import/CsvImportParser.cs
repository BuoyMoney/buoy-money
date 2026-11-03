using System.Globalization;
using BuoyAI.Core.DTOs.Import;
using CsvHelper;
using CsvHelper.Configuration;

namespace BuoyAI.Infrastructure.Services.Import;

/// <summary>Splits delimited text into rows, with the delimiter detected when not given.</summary>
public static class CsvTable
{
  private static readonly char[] Candidates = { ',', ';', '\t', '|' };

  public static char DetectDelimiter(string text)
  {
    var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Take(40).ToList();
    var best = ',';
    var bestScore = 0;
    foreach (var candidate in Candidates)
    {
      var counts = lines.Select(l => CountOutsideQuotes(l, candidate)).Where(c => c > 0).ToList();
      if (counts.Count == 0) continue;
      var modal = counts.GroupBy(c => c).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First();
      var score = modal.Count() * 100 + modal.Key;
      if (score > bestScore)
      {
        bestScore = score;
        best = candidate;
      }
    }
    return best;
  }

  private static int CountOutsideQuotes(string line, char delimiter)
  {
    var inQuotes = false;
    var count = 0;
    foreach (var c in line)
    {
      if (c == '"') inQuotes = !inQuotes;
      else if (c == delimiter && !inQuotes) count++;
    }
    return count;
  }

  public static char ResolveDelimiter(string? requested, string text)
  {
    if (!string.IsNullOrEmpty(requested))
    {
      if (requested == "\\t" || requested.Equals("tab", StringComparison.OrdinalIgnoreCase)) return '\t';
      return requested[0];
    }
    return DetectDelimiter(text);
  }

  public static List<string[]> Read(string text, char delimiter)
  {
    var rows = new List<string[]>();
    var config = new CsvConfiguration(CultureInfo.InvariantCulture)
    {
      HasHeaderRecord = false,
      Delimiter = delimiter.ToString(),
      BadDataFound = null,
      MissingFieldFound = null,
      DetectDelimiter = false,
      TrimOptions = TrimOptions.Trim,
      // A quote in the middle of a bank field is data, not the start of a quoted section.
      Mode = CsvMode.RFC4180
    };
    using var reader = new StringReader(text);
    using var csv = new CsvParser(reader, config);
    while (csv.Read())
    {
      var record = csv.Record;
      if (record == null) continue;
      rows.Add(record.ToArray());
    }
    return rows;
  }

  public static string Cell(string[] row, int? index) =>
    index.HasValue && index.Value >= 0 && index.Value < row.Length ? row[index.Value].Trim() : string.Empty;

  public static bool IsBlank(string[] row) => row.All(c => string.IsNullOrWhiteSpace(c));
}

/// <summary>
/// Reads a bank CSV of unknown layout: finds the header row past any preamble, detects (or takes
/// from the user) the delimiter, columns, date order and number format, and reads each row.
/// </summary>
public static class CsvImportParser
{
  public static ParsedImport Parse(byte[] bytes, ImportMapping mapping)
  {
    var parsed = new ParsedImport { Source = ImportSource.Csv };
    var (text, encodingName) = ImportText.Decode(bytes);
    var delimiter = CsvTable.ResolveDelimiter(mapping.Delimiter, text);
    var table = CsvTable.Read(text, delimiter);

    parsed.Detected.Source = "csv";
    parsed.Detected.Encoding = encodingName;
    parsed.Detected.Delimiter = delimiter == '\t' ? "\\t" : delimiter.ToString();

    var headerIndex = FindHeaderRow(table, mapping);
    if (headerIndex == int.MinValue)
    {
      parsed.Warnings.Add("Could not find a header row with Date and Amount columns. Choose the columns by hand.");
      // Offer the first row's cells (padded to the widest row) so the column pickers can be used.
      var width = table.Take(40).Select(r => r.Length).DefaultIfEmpty(0).Max();
      var first = table.FirstOrDefault(r => !CsvTable.IsBlank(r)) ?? Array.Empty<string>();
      parsed.Detected.Headers = Enumerable.Range(0, width)
        .Select(i => i < first.Length && first[i].Trim().Length > 0 ? first[i].Trim() : $"Column {i + 1}")
        .ToList();
      FillSample(parsed, table, 0);
      return parsed;
    }

    // headerIndex -1 means the user pointed at columns in a file with no header row at all.
    var headers = headerIndex >= 0
      ? table[headerIndex].Select(h => h.Trim()).ToList()
      : Enumerable.Range(1, table.Max(r => r.Length)).Select(i => $"Column {i}").ToList();
    var proposed = DetectColumns(headers);
    var effective = Overlay(proposed, mapping);
    parsed.Detected.Headers = headers;
    parsed.Detected.ProposedMapping = effective;
    FillSample(parsed, table, headerIndex + 1);

    var body = table.Skip(headerIndex + 1).Where(r => !CsvTable.IsBlank(r)).ToList();

    var hasSplitMoney = effective.DebitColumn.HasValue && effective.CreditColumn.HasValue && !effective.AmountColumn.HasValue;
    if (!effective.DateColumn.HasValue || (!effective.AmountColumn.HasValue && !hasSplitMoney))
    {
      parsed.Warnings.Add("Could not tell which columns hold the date and the amount. Choose them by hand.");
      return parsed;
    }

    // Date order and number format: the user's choice wins, otherwise read it off the values.
    var dateSamples = body.Take(500).Select(r => CsvTable.Cell(r, effective.DateColumn)).ToList();
    ImportDateOrder dateOrder;
    if (mapping.DateOrder != ImportDateOrder.Auto)
    {
      dateOrder = mapping.DateOrder;
    }
    else
    {
      var (order, ambiguous) = ImportDateParser.Detect(dateSamples);
      dateOrder = order;
      parsed.DateOrderAmbiguous = ambiguous;
      if (ambiguous)
        parsed.Warnings.Add("Every date in this file could be day-first or month-first (for example 03/04/2026). Pick the order before importing.");
    }
    parsed.Detected.DateOrder = dateOrder.ToString();
    parsed.Detected.DateOrderAmbiguous = parsed.DateOrderAmbiguous;

    var moneySamples = body.Take(500).SelectMany(r => new[]
    {
      CsvTable.Cell(r, effective.AmountColumn),
      CsvTable.Cell(r, effective.DebitColumn),
      CsvTable.Cell(r, effective.CreditColumn)
    }).ToList();
    var numberFormat = mapping.NumberFormat != ImportNumberFormat.Auto
      ? mapping.NumberFormat
      : ImportNumberParser.Detect(moneySamples);
    parsed.Detected.NumberFormat = numberFormat.ToString();
    if (mapping.NumberFormat == ImportNumberFormat.Auto && numberFormat == ImportNumberFormat.European)
      parsed.Warnings.Add("Amounts in this file use a decimal comma (1.234,56). They were read that way.");

    var accountsByKey = new Dictionary<string, ParsedImportAccount>(StringComparer.OrdinalIgnoreCase);
    var occurrences = new Dictionary<string, int>();
    var rowId = 0;
    foreach (var cells in body)
    {
      var row = new ParsedImportRow { RowId = rowId++ };

      if (effective.AccountColumn.HasValue)
      {
        var accountName = CsvTable.Cell(cells, effective.AccountColumn);
        row.AccountKey = accountName;
        if (accountName.Length > 0 && !accountsByKey.ContainsKey(accountName))
        {
          var account = new ParsedImportAccount { Key = accountName, Name = accountName, Type = ImportAccountTypes.GuessFromName(accountName) };
          accountsByKey[accountName] = account;
          parsed.Accounts.Add(account);
        }
      }

      var dateText = CsvTable.Cell(cells, effective.DateColumn);
      if (!ImportDateParser.TryParse(dateText, dateOrder, out var date))
        row.Error = "The date could not be read.";
      else
        row.Date = date;

      decimal amount = 0;
      var amountOk = false;
      if (hasSplitMoney)
      {
        var debitOk = ImportNumberParser.TryParse(CsvTable.Cell(cells, effective.DebitColumn), numberFormat, out var debit) && debit != 0;
        var creditOk = ImportNumberParser.TryParse(CsvTable.Cell(cells, effective.CreditColumn), numberFormat, out var credit) && credit != 0;
        if (debitOk) { amount = -Math.Abs(debit); amountOk = true; }
        else if (creditOk) { amount = Math.Abs(credit); amountOk = true; }
      }
      else
      {
        amountOk = ImportNumberParser.TryParse(CsvTable.Cell(cells, effective.AmountColumn), numberFormat, out amount);
      }
      if (!amountOk) row.Error ??= "The amount could not be read.";
      else row.Amount = mapping.FlipSign ? -amount : amount;

      row.Payee = CsvTable.Cell(cells, effective.DescriptionColumn);
      var memo = CsvTable.Cell(cells, effective.MemoColumn);
      row.Memo = memo.Length > 0 ? memo : null;
      row.OriginalDescription = row.Payee.Length > 0 ? row.Payee : null;
      if (row.Payee.Length == 0 && row.Memo != null) row.Payee = row.Memo;

      var category = CsvTable.Cell(cells, effective.CategoryColumn);
      if (category.Length > 0) row.CategoryName = category;

      var id = CsvTable.Cell(cells, effective.IdColumn);
      if (id.Length > 0) row.SourceId = id;

      if (row.Error == null)
      {
        var occKey = $"{row.AccountKey}|{row.Date:yyyyMMdd}|{row.Amount}|{row.Payee.ToLowerInvariant()}";
        row.Occurrence = occurrences.GetValueOrDefault(occKey);
        occurrences[occKey] = row.Occurrence + 1;
      }
      parsed.Rows.Add(row);
    }

    foreach (var name in parsed.Rows.Where(r => r.CategoryName != null).Select(r => r.CategoryName!).Distinct(StringComparer.OrdinalIgnoreCase))
      parsed.Categories.Add(new ParsedImportCategory { Group = string.Empty, Name = name, IsIncome = false });

    return parsed;
  }

  /// <summary>The first row that looks like headers: a date column plus an amount, debit or credit column.</summary>
  private static int FindHeaderRow(List<string[]> table, ImportMapping mapping)
  {
    for (var i = 0; i < Math.Min(table.Count, 40); i++)
    {
      var lower = table[i].Select(c => c.Trim().ToLowerInvariant()).ToList();
      if (lower.Count < 2) continue;
      var hasDate = lower.Any(c => c.Contains("date") || c == "posted");
      var hasMoney = lower.Any(c => c.Contains("amount") || c.Contains("debit") || c.Contains("credit")
                                    || c == "withdrawal" || c == "deposit" || c.Contains("withdrawals") || c.Contains("deposits"));
      if (hasDate && hasMoney) return i;
    }
    // The user named the columns by hand (headers in another language, say): the header is the
    // row above the first row whose date cell reads as a date.
    if (mapping.DateColumn.HasValue && (mapping.AmountColumn.HasValue || (mapping.DebitColumn.HasValue && mapping.CreditColumn.HasValue)))
    {
      for (var i = 0; i < Math.Min(table.Count, 60); i++)
      {
        var cell = CsvTable.Cell(table[i], mapping.DateColumn);
        if (cell.Length > 0 && (ImportDateParser.TryParse(cell, ImportDateOrder.MonthFirst, out _)
                                || ImportDateParser.TryParse(cell, ImportDateOrder.DayFirst, out _)))
          return i - 1;
      }
    }
    return int.MinValue;
  }

  private static ImportMapping Overlay(ImportMapping detected, ImportMapping user) => new()
  {
    Delimiter = user.Delimiter,
    DateOrder = user.DateOrder,
    NumberFormat = user.NumberFormat,
    FlipSign = user.FlipSign,
    DateColumn = user.DateColumn ?? detected.DateColumn,
    AmountColumn = user.AmountColumn ?? (user.DebitColumn.HasValue || user.CreditColumn.HasValue ? null : detected.AmountColumn),
    DebitColumn = user.DebitColumn ?? (user.AmountColumn.HasValue ? null : detected.DebitColumn),
    CreditColumn = user.CreditColumn ?? (user.AmountColumn.HasValue ? null : detected.CreditColumn),
    DescriptionColumn = user.DescriptionColumn ?? detected.DescriptionColumn,
    MemoColumn = user.MemoColumn ?? detected.MemoColumn,
    CategoryColumn = user.CategoryColumn ?? detected.CategoryColumn,
    AccountColumn = user.AccountColumn ?? detected.AccountColumn,
    IdColumn = user.IdColumn ?? detected.IdColumn
  };

  public static ImportMapping DetectColumns(List<string> headers)
  {
    var map = new ImportMapping();
    for (var i = 0; i < headers.Count; i++)
    {
      var h = headers[i].Trim().ToLowerInvariant();
      if (h.Length == 0) continue;
      var isBalance = h.Contains("balance");

      if (!map.DateColumn.HasValue && (h.Contains("date") || h == "posted") && !h.Contains("update"))
        map.DateColumn = i;
      else if (!map.AmountColumn.HasValue && h.Contains("amount") && !h.Contains("debit") && !h.Contains("credit") && !isBalance)
        map.AmountColumn = i;
      else if (!map.DebitColumn.HasValue && (h.Contains("debit") || h == "withdrawal" || h == "withdrawals") && !isBalance)
        map.DebitColumn = i;
      else if (!map.CreditColumn.HasValue && (h.Contains("credit") || h == "deposit" || h == "deposits") && !isBalance && !h.Contains("card"))
        map.CreditColumn = i;
      else if (!map.IdColumn.HasValue && (h == "id" || h == "transaction id" || h == "fitid" || h == "reference id" || h == "unique id"))
        map.IdColumn = i;
      else if (!map.AccountColumn.HasValue && (h == "account" || h == "account name"))
        map.AccountColumn = i;
      else if (!map.CategoryColumn.HasValue && (h == "category" || h == "categories"))
        map.CategoryColumn = i;
      else if (!map.DescriptionColumn.HasValue
               && (h.Contains("description") || h == "name" || h == "payee" || h == "merchant" || h == "details" || h == "narrative"))
        map.DescriptionColumn = i;
      else if (!map.MemoColumn.HasValue && (h == "memo" || h == "note" || h == "notes" || h == "reference"))
        map.MemoColumn = i;
    }

    if (!map.DescriptionColumn.HasValue && map.MemoColumn.HasValue)
    {
      // A memo-only file: the memo is the description.
      map.DescriptionColumn = map.MemoColumn;
      map.MemoColumn = null;
    }
    // A single Amount column wins over a Debit/Credit pair that is only a type label.
    if (map.AmountColumn.HasValue)
    {
      map.DebitColumn = null;
      map.CreditColumn = null;
    }
    return map;
  }

  private static void FillSample(ParsedImport parsed, List<string[]> table, int fromRow)
  {
    foreach (var row in table.Skip(fromRow).Where(r => !CsvTable.IsBlank(r)).Take(5))
      parsed.Detected.SampleRows.Add(row.ToList());
  }
}

public static class ImportAccountTypes
{
  public static string GuessFromName(string name)
  {
    var n = name.ToLowerInvariant();
    if (n.Contains("visa") || n.Contains("mastercard") || n.Contains("amex") || n.Contains("discover")
        || n.Contains("credit") || n.Contains("card")) return "CreditCard";
    if (n.Contains("saving")) return "Savings";
    if (n.Contains("cash") || n.Contains("wallet")) return "Cash";
    if (n.Contains("invest") || n.Contains("brokerage") || n.Contains("401k") || n.Contains("ira")) return "Investment";
    return "Checking";
  }
}
