using System.Text.Json.Serialization;

namespace BuoyAI.Core.DTOs.Import;

/// <summary>Where a staged file came from. <c>Auto</c> means work it out from the file.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImportSource
{
  Auto = 0,
  Csv = 1,
  Ofx = 2,
  Ynab = 3,
  Actual = 4
}

/// <summary>The order of day, month and year in a text date such as 01/02/2026.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImportDateOrder
{
  Auto = 0,
  MonthFirst = 1,
  DayFirst = 2,
  YearFirst = 3
}

/// <summary>How a number writes its thousands and decimal separators.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImportNumberFormat
{
  /// <summary>Work it out from the values in the file.</summary>
  Auto = 0,
  /// <summary>1,234.56</summary>
  Us = 1,
  /// <summary>1.234,56 or 1 234,56</summary>
  European = 2
}

/// <summary>The classification the preview gives every row. Strings so the JSON reads plainly.</summary>
public static class ImportRowClass
{
  public const string New = "new";
  /// <summary>Same source id already stored for this account. Always skipped.</summary>
  public const string DuplicateExact = "duplicateExact";
  /// <summary>Same amount, close date and a similar payee as a stored transaction. Skipped by default.</summary>
  public const string DuplicateLikely = "duplicateLikely";
  /// <summary>Same amount and a close date as a stored transaction, but the payees differ. Imported by default.</summary>
  public const string PossibleDuplicate = "possibleDuplicate";
  public const string Invalid = "invalid";
}

/// <summary>
/// The user's choices about how to read a file. Every field is optional; what is left out is
/// detected, and the preview reports what it detected so the user can correct it.
/// Column numbers are zero-based positions in the header row.
/// </summary>
public class ImportMapping
{
  /// <summary>One character: comma, semicolon, tab or pipe. Null detects it.</summary>
  public string? Delimiter { get; set; }
  public ImportDateOrder DateOrder { get; set; } = ImportDateOrder.Auto;
  public ImportNumberFormat NumberFormat { get; set; } = ImportNumberFormat.Auto;

  /// <summary>Multiply every amount by -1 (for card exports where charges are positive).</summary>
  public bool FlipSign { get; set; }

  public int? DateColumn { get; set; }
  public int? AmountColumn { get; set; }
  public int? DebitColumn { get; set; }
  public int? CreditColumn { get; set; }
  public int? DescriptionColumn { get; set; }
  public int? MemoColumn { get; set; }
  public int? CategoryColumn { get; set; }
  public int? AccountColumn { get; set; }
  public int? IdColumn { get; set; }
}

public class ImportPreviewRequest
{
  public ImportSource Source { get; set; } = ImportSource.Auto;

  /// <summary>Existing account to import into. Null lets the preview propose creating one.</summary>
  public int? AccountId { get; set; }
  public ImportMapping Mapping { get; set; } = new();
}

public class ImportFileInput
{
  public string FileName { get; set; } = string.Empty;
  public byte[] Content { get; set; } = Array.Empty<byte>();
}

public class ImportDetectedInfo
{
  public string Source { get; set; } = string.Empty;
  public string? Delimiter { get; set; }
  public string? Encoding { get; set; }
  public string? DateOrder { get; set; }
  public bool DateOrderAmbiguous { get; set; }
  public string? NumberFormat { get; set; }
  public List<string> Headers { get; set; } = new();
  public List<List<string>> SampleRows { get; set; } = new();
  public ImportMapping ProposedMapping { get; set; } = new();
}

public class ImportAccountPreview
{
  public string SourceKey { get; set; } = string.Empty;
  public string SourceName { get; set; } = string.Empty;
  /// <summary>create or match.</summary>
  public string ProposedAction { get; set; } = "create";
  public int? MatchedAccountId { get; set; }
  public string ProposedType { get; set; } = "Checking";
  public bool Closed { get; set; }
  public int RowCount { get; set; }
  /// <summary>Rows in this account by class, so the UI can tell what skipping the account removes.</summary>
  public int NewCount { get; set; }
  public int DuplicateLikelyCount { get; set; }
  public int PossibleDuplicateCount { get; set; }
}

public class ImportCategoryPreview
{
  public string SourceGroup { get; set; } = string.Empty;
  public string SourceName { get; set; } = string.Empty;
  public bool IsIncome { get; set; }
  public int? ProposedCategoryId { get; set; }
  public string? ProposedCategoryName { get; set; }
  public string ProposedNewName { get; set; } = string.Empty;
  public int RowCount { get; set; }
}

public class ImportBudgetPreview
{
  public List<string> Months { get; set; } = new();
  public string LatestMonth { get; set; } = string.Empty;
  public int CategoriesWithAmounts { get; set; }
  public decimal LatestMonthTotal { get; set; }
}

public class ImportSummary
{
  public int Total { get; set; }
  public int New { get; set; }
  public int DuplicateExact { get; set; }
  public int DuplicateLikely { get; set; }
  public int PossibleDuplicate { get; set; }
  public int Invalid { get; set; }
  public DateOnly? FirstDate { get; set; }
  public DateOnly? LastDate { get; set; }
  public decimal TotalInflow { get; set; }
  public decimal TotalOutflow { get; set; }
}

public class ImportRowPreview
{
  public int RowId { get; set; }
  public string? AccountKey { get; set; }
  public DateOnly? Date { get; set; }
  public decimal Amount { get; set; }
  public string Payee { get; set; } = string.Empty;
  public string? Memo { get; set; }
  public string? Category { get; set; }
  public string Class { get; set; } = ImportRowClass.New;
  public int? MatchedTransactionId { get; set; }
  public string? Reason { get; set; }
}

public class ImportPreviewResult
{
  public string ImportId { get; set; } = string.Empty;
  public DateTime ExpiresAt { get; set; }
  public ImportDetectedInfo Detected { get; set; } = new();
  public List<ImportAccountPreview> Accounts { get; set; } = new();
  public List<ImportCategoryPreview> Categories { get; set; } = new();
  public ImportBudgetPreview? Budget { get; set; }
  public ImportSummary Summary { get; set; } = new();

  /// <summary>The first rows (at most 200). Use the rows endpoint for the rest.</summary>
  public List<ImportRowPreview> Rows { get; set; } = new();
  public List<string> Warnings { get; set; } = new();

  /// <summary>True when day and month cannot be told apart. Commit is refused until the user picks an order.</summary>
  public bool NeedsDateOrderChoice { get; set; }
}

public class ImportRowsPage
{
  public int Page { get; set; }
  public int PageSize { get; set; }
  public int Total { get; set; }
  public List<ImportRowPreview> Rows { get; set; } = new();
}

public class ImportAccountMapping
{
  public string SourceKey { get; set; } = string.Empty;
  /// <summary>create, match or skip.</summary>
  public string Action { get; set; } = "create";
  public int? AccountId { get; set; }
  public string? Name { get; set; }
  /// <summary>Checking, Savings, CreditCard, Cash, Investment or Other.</summary>
  public string? Type { get; set; }
}

public class ImportCategoryMapping
{
  public string SourceGroup { get; set; } = string.Empty;
  public string SourceName { get; set; } = string.Empty;
  /// <summary>map, create or none.</summary>
  public string Action { get; set; } = "map";
  public int? CategoryId { get; set; }
  public string? Name { get; set; }
}

public class ImportCommitRequest
{
  /// <summary>When empty, the preview's proposals are used.</summary>
  public List<ImportAccountMapping> AccountMappings { get; set; } = new();
  public List<ImportCategoryMapping> CategoryMappings { get; set; } = new();

  /// <summary>Row classes to import. Defaults to new and possibleDuplicate.</summary>
  public List<string>? IncludeClasses { get; set; }

  /// <summary>Per-row choices: the key is the row id, the value is skip or import.</summary>
  public Dictionary<int, string> Overrides { get; set; } = new();

  /// <summary>Create a budget from the latest month of the file budget, when it has one.</summary>
  public bool CreateBudget { get; set; }
}

public class ImportCommitResult
{
  public string BatchId { get; set; } = string.Empty;
  public int Imported { get; set; }
  public int SkippedDuplicates { get; set; }
  public int SkippedInvalid { get; set; }
  public int SkippedByChoice { get; set; }
  public int AccountsCreated { get; set; }
  public int CategoriesCreated { get; set; }
  public bool BudgetCreated { get; set; }
  public int? BudgetId { get; set; }
  public int RulesApplied { get; set; }
  public int QueuedForAi { get; set; }
  public List<int> AccountIds { get; set; } = new();
  public List<string> Warnings { get; set; } = new();
  public DateTime UndoableUntil { get; set; }
}

public class ImportUndoResult
{
  public int TransactionsRemoved { get; set; }
  public int AccountsRemoved { get; set; }
}

/// <summary>Raised for problems the user can fix (bad file, no permission, expired preview).</summary>
public class ImportException : Exception
{
  public int StatusCode { get; }

  public ImportException(string message, int statusCode = 400) : base(message)
  {
    StatusCode = statusCode;
  }
}
