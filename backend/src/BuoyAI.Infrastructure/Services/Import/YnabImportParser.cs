using System.Globalization;
using System.Text.RegularExpressions;
using BuoyAI.Core.DTOs.Import;

namespace BuoyAI.Infrastructure.Services.Import;

/// <summary>
/// Reads a YNAB export: the Register CSV (every transaction) and, when given, the Plan CSV (a
/// budget for each month). Either may come alone. YNAB writes dates and numbers in the plan's
/// own locale, so both are detected from the file and can be overridden.
/// </summary>
public static class YnabImportParser
{
  private static readonly Regex SplitMemo = new(@"^\(Split (\d+)/(\d+)\)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
  private static readonly HashSet<string> SkippedGroups = new(StringComparer.OrdinalIgnoreCase)
  {
    "Internal Master Category", "Credit Card Payments"
  };

  public enum YnabFileKind { None, Register, Plan }

  public static YnabFileKind Sniff(byte[] bytes)
  {
    var (text, _) = ImportText.Decode(bytes);
    var firstLine = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.ToLowerInvariant() ?? string.Empty;
    var plain = firstLine.Replace("\"", string.Empty);
    if (plain.Contains("outflow") && plain.Contains("inflow") && plain.Contains("payee")) return YnabFileKind.Register;
    if (plain.Contains("budgeted") && plain.Contains("activity") && plain.Contains("available")) return YnabFileKind.Plan;
    return YnabFileKind.None;
  }

  public static ParsedImport Parse(IReadOnlyList<byte[]> files, ImportMapping mapping)
  {
    var parsed = new ParsedImport { Source = ImportSource.Ynab };
    parsed.Detected.Source = "ynab";

    byte[]? register = null;
    byte[]? plan = null;
    foreach (var file in files)
    {
      switch (Sniff(file))
      {
        case YnabFileKind.Register: register ??= file; break;
        case YnabFileKind.Plan: plan ??= file; break;
      }
    }
    if (register == null && plan == null)
    {
      parsed.Warnings.Add("This does not look like a YNAB export. Expected the Register and Plan files that YNAB's Export Plan produces.");
      return parsed;
    }

    var accountsByName = new Dictionary<string, ParsedImportAccount>(StringComparer.OrdinalIgnoreCase);
    if (register != null) ParseRegister(register, mapping, parsed, accountsByName);
    if (plan != null) ParsePlan(plan, mapping, parsed);
    if (plan == null && register != null)
      parsed.Warnings.Add("No Plan file was included, so budgets were not imported. Category targets are not part of YNAB's export.");

    return parsed;
  }

  private static void ParseRegister(byte[] bytes, ImportMapping mapping, ParsedImport parsed, Dictionary<string, ParsedImportAccount> accountsByName)
  {
    var (text, encodingName) = ImportText.Decode(bytes);
    var delimiter = CsvTable.ResolveDelimiter(mapping.Delimiter, text);
    var table = CsvTable.Read(text, delimiter);
    parsed.Detected.Encoding = encodingName;
    parsed.Detected.Delimiter = delimiter == '\t' ? "\\t" : delimiter.ToString();
    if (table.Count < 2) return;

    var headers = table[0].Select(h => h.Trim().Trim('﻿')).ToList();
    parsed.Detected.Headers = headers;
    int Col(params string[] names) =>
      headers.FindIndex(h => names.Any(n => h.Equals(n, StringComparison.OrdinalIgnoreCase)));

    var accountCol = Col("Account");
    var dateCol = Col("Date");
    var payeeCol = Col("Payee");
    var groupCatCol = Col("Category Group/Category");
    var groupCol = Col("Category Group");
    var categoryCol = Col("Category");
    var memoCol = Col("Memo");
    var outflowCol = Col("Outflow");
    var inflowCol = Col("Inflow");
    if (accountCol < 0 || dateCol < 0 || outflowCol < 0 || inflowCol < 0)
    {
      parsed.Warnings.Add("The Register file is missing a required column (Account, Date, Outflow or Inflow).");
      return;
    }

    var body = table.Skip(1).Where(r => !CsvTable.IsBlank(r)).ToList();
    foreach (var r in body.Take(5)) parsed.Detected.SampleRows.Add(r.ToList());

    ImportDateOrder dateOrder;
    if (mapping.DateOrder != ImportDateOrder.Auto)
    {
      dateOrder = mapping.DateOrder;
    }
    else
    {
      var (order, ambiguous) = ImportDateParser.Detect(body.Take(1000).Select(r => CsvTable.Cell(r, dateCol)));
      dateOrder = order;
      parsed.DateOrderAmbiguous = ambiguous;
      if (ambiguous)
        parsed.Warnings.Add("Every date in this file could be day-first or month-first (for example 03/04/2026). Pick the order your YNAB plan uses before importing.");
    }
    parsed.Detected.DateOrder = dateOrder.ToString();
    parsed.Detected.DateOrderAmbiguous = parsed.DateOrderAmbiguous;

    var numberFormat = mapping.NumberFormat != ImportNumberFormat.Auto
      ? mapping.NumberFormat
      : ImportNumberParser.Detect(body.Take(1000).SelectMany(r => new[] { CsvTable.Cell(r, outflowCol), CsvTable.Cell(r, inflowCol) }));
    parsed.Detected.NumberFormat = numberFormat.ToString();

    var occurrences = new Dictionary<string, int>();
    var splitRows = 0;
    var rowId = 0;
    foreach (var cells in body)
    {
      var row = new ParsedImportRow { RowId = rowId++ };

      var accountName = CsvTable.Cell(cells, accountCol);
      row.AccountKey = accountName;
      if (accountName.Length > 0 && !accountsByName.ContainsKey(accountName))
      {
        var account = new ParsedImportAccount { Key = accountName, Name = accountName, Type = ImportAccountTypes.GuessFromName(accountName) };
        accountsByName[accountName] = account;
        parsed.Accounts.Add(account);
      }

      if (!ImportDateParser.TryParse(CsvTable.Cell(cells, dateCol), dateOrder, out var date))
        row.Error = "The date could not be read.";
      else
        row.Date = date;

      var outText = CsvTable.Cell(cells, outflowCol);
      var inText = CsvTable.Cell(cells, inflowCol);
      decimal outflow = 0, inflow = 0;
      var outOk = outText.Length == 0 || ImportNumberParser.TryParse(outText, numberFormat, out outflow);
      var inOk = inText.Length == 0 || ImportNumberParser.TryParse(inText, numberFormat, out inflow);
      if (!outOk || !inOk) row.Error ??= "The amount could not be read.";
      else row.Amount = inflow - outflow;
      if (mapping.FlipSign) row.Amount = -row.Amount;

      var payee = CsvTable.Cell(cells, payeeCol);
      var memo = CsvTable.Cell(cells, memoCol);

      // A transfer names the other account: "Transfer : Savings".
      var transferMatch = Regex.Match(payee, @"^Transfer\s*:\s*(.+)$", RegexOptions.IgnoreCase);
      if (transferMatch.Success)
      {
        row.TransferAccountName = transferMatch.Groups[1].Value.Trim();
        row.Payee = payee;
      }
      else
      {
        row.Payee = payee;
      }

      if (payee.Equals("Starting Balance", StringComparison.OrdinalIgnoreCase))
      {
        row.IsStartingBalance = true;
        memo = memo.Length > 0 ? $"Starting balance. {memo}" : "Starting balance";
      }

      var split = SplitMemo.Match(memo);
      if (split.Success)
      {
        splitRows++;
        memo = memo[split.Length..].Trim();
      }

      row.Memo = memo.Length > 0 ? memo : null;
      row.OriginalDescription = payee.Length > 0 ? payee : null;

      // Category: prefer the two separate columns, fall back to splitting "Group/Category".
      var group = CsvTable.Cell(cells, groupCol);
      var category = CsvTable.Cell(cells, categoryCol);
      if (category.Length == 0 && groupCatCol >= 0)
      {
        var combined = CsvTable.Cell(cells, groupCatCol);
        var slash = combined.IndexOf('/');
        if (slash > 0)
        {
          group = combined[..slash].Trim();
          category = combined[(slash + 1)..].Trim();
        }
      }
      if (category.Length > 0 && !SkippedGroups.Contains(group) && row.TransferAccountName == null)
      {
        row.CategoryGroup = group;
        row.CategoryName = category;
      }

      if (row.Error == null)
      {
        var occKey = $"{row.AccountKey}|{row.Date:yyyyMMdd}|{row.Amount}|{row.Payee.ToLowerInvariant()}";
        row.Occurrence = occurrences.GetValueOrDefault(occKey);
        occurrences[occKey] = row.Occurrence + 1;
      }
      parsed.Rows.Add(row);
    }

    // A transfer is only a transfer when the other side is in the file too.
    foreach (var row in parsed.Rows.Where(r => r.TransferAccountName != null))
      row.IsTransfer = accountsByName.ContainsKey(row.TransferAccountName!);

    if (splitRows > 0)
      parsed.Warnings.Add($"{splitRows} rows are parts of split transactions. They were imported as separate rows, each with its own category.");

    foreach (var cat in parsed.Rows
               .Where(r => r.CategoryName != null)
               .Select(r => (r.CategoryGroup ?? string.Empty, r.CategoryName!))
               .Distinct())
    {
      parsed.Categories.Add(new ParsedImportCategory
      {
        Group = cat.Item1,
        Name = cat.Item2,
        IsIncome = IsIncomeGroup(cat.Item1)
      });
    }
  }

  private static bool IsIncomeGroup(string group) =>
    group.Equals("Inflow", StringComparison.OrdinalIgnoreCase) || group.Equals("Income", StringComparison.OrdinalIgnoreCase);

  private static void ParsePlan(byte[] bytes, ImportMapping mapping, ParsedImport parsed)
  {
    var (text, _) = ImportText.Decode(bytes);
    var delimiter = CsvTable.ResolveDelimiter(mapping.Delimiter, text);
    var table = CsvTable.Read(text, delimiter);
    if (table.Count < 2) return;

    var headers = table[0].Select(h => h.Trim().Trim('﻿')).ToList();
    int Col(string name) => headers.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
    var monthCol = Col("Month");
    var groupCol = Col("Category Group");
    var categoryCol = Col("Category");
    var budgetedCol = Col("Budgeted");
    var availableCol = Col("Available");
    var groupCatCol = Col("Category Group/Category");
    if (monthCol < 0 || budgetedCol < 0 || (categoryCol < 0 && groupCatCol < 0))
    {
      parsed.Warnings.Add("The Plan file is missing a required column (Month, Category or Budgeted).");
      return;
    }

    var body = table.Skip(1).Where(r => !CsvTable.IsBlank(r)).ToList();
    var numberFormat = mapping.NumberFormat != ImportNumberFormat.Auto
      ? mapping.NumberFormat
      : parsed.Detected.NumberFormat is { } fromRegister && Enum.TryParse<ImportNumberFormat>(fromRegister, out var carried)
        ? carried
        : ImportNumberParser.Detect(body.Take(1000).Select(r => CsvTable.Cell(r, budgetedCol)));

    foreach (var cells in body)
    {
      var group = CsvTable.Cell(cells, groupCol);
      var category = CsvTable.Cell(cells, categoryCol);
      if (category.Length == 0 && groupCatCol >= 0)
      {
        var combined = CsvTable.Cell(cells, groupCatCol);
        var slash = combined.IndexOf('/');
        if (slash > 0) { group = combined[..slash].Trim(); category = combined[(slash + 1)..].Trim(); }
      }
      if (category.Length == 0 || SkippedGroups.Contains(group)) continue;

      if (!TryParseMonth(CsvTable.Cell(cells, monthCol), mapping.DateOrder, out var month)) continue;
      if (!ImportNumberParser.TryParse(CsvTable.Cell(cells, budgetedCol), numberFormat, out var budgeted)) continue;
      decimal? available = null;
      if (ImportNumberParser.TryParse(CsvTable.Cell(cells, availableCol), numberFormat, out var avail)) available = avail;

      parsed.Budgets.Add(new ParsedBudgetEntry
      {
        Month = month,
        Group = group,
        Category = category,
        Budgeted = budgeted,
        Available = available
      });
    }
  }

  private static readonly string[] MonthFormats = { "MMM yyyy", "MMMM yyyy", "yyyy-MM", "yyyy/MM", "MM/yyyy", "M/yyyy", "MMM-yyyy", "MMM yy" };

  public static bool TryParseMonth(string text, ImportDateOrder order, out string month)
  {
    month = string.Empty;
    var s = text.Trim();
    if (DateTime.TryParseExact(s, MonthFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt))
    {
      month = dt.ToString("yyyy-MM", CultureInfo.InvariantCulture);
      return true;
    }
    if (ImportDateParser.TryParse(s, order == ImportDateOrder.Auto ? ImportDateOrder.MonthFirst : order, out var date))
    {
      month = date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
      return true;
    }
    return false;
  }
}
