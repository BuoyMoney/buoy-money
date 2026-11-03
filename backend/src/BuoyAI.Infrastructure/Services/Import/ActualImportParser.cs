using System.IO.Compression;
using BuoyAI.Core.DTOs.Import;
using Microsoft.Data.Sqlite;

namespace BuoyAI.Infrastructure.Services.Import;

/// <summary>
/// Reads an Actual Budget export: a zip holding <c>db.sqlite</c> and <c>metadata.json</c>.
/// Amounts are integer cents, dates are YYYYMMDD integers, and every table carries a
/// <c>tombstone</c> flag for soft deletes. Column names differ between Actual versions, so each
/// table is probed before it is queried.
/// </summary>
public static class ActualImportParser
{
  /// <summary>Largest database we will unpack. A zip that claims or expands past this is refused.</summary>
  public const long MaxUnpackedBytes = 200L * 1024 * 1024;

  public static bool LooksLikeActualZip(byte[] bytes)
  {
    if (!IsZip(bytes)) return false;
    try
    {
      using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
      return archive.Entries.Any(e => e.FullName.Equals("db.sqlite", StringComparison.OrdinalIgnoreCase));
    }
    catch (InvalidDataException)
    {
      return false;
    }
  }

  public static bool IsZip(byte[] bytes) =>
    bytes.Length > 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && (bytes[2] == 3 || bytes[2] == 5) && (bytes[3] == 4 || bytes[3] == 6);

  public static ParsedImport Parse(byte[] zipBytes)
  {
    var parsed = new ParsedImport { Source = ImportSource.Actual };
    parsed.Detected.Source = "actual";

    var tempPath = Path.Combine(Path.GetTempPath(), $"buoy-actual-{Guid.NewGuid():N}.sqlite");
    try
    {
      UnpackDatabase(zipBytes, tempPath);
      using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
      {
        DataSource = tempPath,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false
      }.ToString());
      connection.Open();
      ReadDatabase(connection, parsed);
    }
    finally
    {
      try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (IOException) { }
    }
    return parsed;
  }

  /// <summary>Copies only the db.sqlite entry to a temp file, counting bytes so a zip bomb stops at the cap.</summary>
  private static void UnpackDatabase(byte[] zipBytes, string tempPath)
  {
    ZipArchive archive;
    try
    {
      archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
    }
    catch (InvalidDataException)
    {
      throw new ImportException("That file is not a valid zip.");
    }

    using (archive)
    {
      var entry = archive.Entries.FirstOrDefault(e => e.FullName.Equals("db.sqlite", StringComparison.OrdinalIgnoreCase))
                  ?? throw new ImportException("This zip does not contain db.sqlite, so it is not an Actual Budget export.");
      if (entry.Length > MaxUnpackedBytes)
        throw new ImportException("The Actual database inside this zip is too large to import.");

      using var input = entry.Open();
      using var output = File.Create(tempPath);
      var buffer = new byte[81920];
      long total = 0;
      int read;
      while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
      {
        total += read;
        if (total > MaxUnpackedBytes)
          throw new ImportException("The Actual database inside this zip is too large to import.");
        output.Write(buffer, 0, read);
      }
    }
  }

  private static void ReadDatabase(SqliteConnection db, ParsedImport parsed)
  {
    if (!TableExists(db, "transactions") || !TableExists(db, "accounts"))
      throw new ImportException("This database does not look like an Actual Budget file.");

    // Accounts.
    var accountCols = Columns(db, "accounts");
    var accountIdToKey = new Dictionary<string, string>();
    foreach (var row in Query(db, "accounts", accountCols, "id", "name", "offbudget", "closed", "type", "subtype", "tombstone"))
    {
      if (IsTombstone(row)) continue;
      var id = Str(row, "id");
      var name = Str(row, "name");
      if (id.Length == 0) continue;
      var account = new ParsedImportAccount
      {
        Key = id,
        Name = name.Length > 0 ? name : "Account",
        Closed = Int(row, "closed") == 1,
        Type = GuessAccountType(name, Str(row, "type"), Str(row, "subtype"), Int(row, "offbudget") == 1)
      };
      parsed.Accounts.Add(account);
      accountIdToKey[id] = id;
    }

    // Payees and merges. A merged payee id points at the one that survived.
    var payeeNames = new Dictionary<string, string>();
    var payeeTransferAccount = new Dictionary<string, string>();
    if (TableExists(db, "payees"))
    {
      foreach (var row in Query(db, "payees", Columns(db, "payees"), "id", "name", "transfer_acct", "tombstone"))
      {
        var id = Str(row, "id");
        if (id.Length == 0) continue;
        payeeNames[id] = Str(row, "name");
        var transfer = Str(row, "transfer_acct");
        if (transfer.Length > 0) payeeTransferAccount[id] = transfer;
      }
    }
    var payeeMerge = MappingTable(db, "payee_mapping");

    // Categories and groups.
    var groupNames = new Dictionary<string, string>();
    var groupIncome = new Dictionary<string, bool>();
    if (TableExists(db, "category_groups"))
    {
      foreach (var row in Query(db, "category_groups", Columns(db, "category_groups"), "id", "name", "is_income", "tombstone"))
      {
        if (IsTombstone(row)) continue;
        var id = Str(row, "id");
        groupNames[id] = Str(row, "name");
        groupIncome[id] = Int(row, "is_income") == 1;
      }
    }
    var categoryInfo = new Dictionary<string, ParsedImportCategory>();
    if (TableExists(db, "categories"))
    {
      var catCols = Columns(db, "categories");
      foreach (var row in Query(db, "categories", catCols, "id", "name", "cat_group", "is_income", "tombstone"))
      {
        if (IsTombstone(row)) continue;
        var id = Str(row, "id");
        var group = groupNames.GetValueOrDefault(Str(row, "cat_group"), string.Empty);
        var isIncome = Int(row, "is_income") == 1 || groupIncome.GetValueOrDefault(Str(row, "cat_group"));
        categoryInfo[id] = new ParsedImportCategory { Group = group, Name = Str(row, "name"), IsIncome = isIncome };
      }
    }
    var categoryMerge = MappingTable(db, "category_mapping");

    ParsedImportCategory? ResolveCategory(string id)
    {
      if (id.Length == 0) return null;
      if (categoryMerge.TryGetValue(id, out var target)) id = target;
      return categoryInfo.GetValueOrDefault(id);
    }

    string ResolvePayeeId(string id) => payeeMerge.TryGetValue(id, out var target) ? target : id;

    // Transactions. Parents of a split carry the full amount and no category; their children
    // carry the pieces, so the parents are skipped and the children imported.
    var txCols = Columns(db, "transactions");
    var catCol = Pick(txCols, "category_id", "category");
    var payeeCol = Pick(txCols, "payee_id", "description");
    var parentFlag = Pick(txCols, "is_parent", "isParent");
    var childFlag = Pick(txCols, "is_child", "isChild");
    var parentIdCol = Pick(txCols, "parent_id");

    var wanted = new[] { "id", "acct", "amount", "date", "notes", "financial_id", "imported_description", "starting_balance_flag", "tombstone" }
      .Concat(new[] { catCol, payeeCol, parentFlag, childFlag, parentIdCol }.Where(c => c != null).Cast<string>())
      .ToArray();

    var raw = Query(db, "transactions", txCols, wanted).Where(r => !IsTombstone(r)).ToList();
    var parentRows = parentFlag == null
      ? new Dictionary<string, Dictionary<string, string>>()
      : raw.Where(r => Int(r, parentFlag) == 1).ToDictionary(r => Str(r, "id"), r => r);

    var splitChildren = 0;
    var unknownAccountRows = 0;
    var occurrences = new Dictionary<string, int>();
    var rowId = 0;
    foreach (var r in raw)
    {
      if (parentFlag != null && Int(r, parentFlag) == 1) continue;

      var acct = Str(r, "acct");
      if (!accountIdToKey.TryGetValue(acct, out var accountKey))
      {
        unknownAccountRows++;
        continue;
      }

      var isChild = childFlag != null && Int(r, childFlag) == 1;
      Dictionary<string, string>? parent = null;
      if (isChild && parentIdCol != null && parentRows.TryGetValue(Str(r, parentIdCol), out var p)) parent = p;
      if (isChild) splitChildren++;

      var row = new ParsedImportRow
      {
        RowId = rowId++,
        AccountKey = accountKey,
        SourceId = Str(r, "id"),
        Amount = Int(r, "amount") / 100m
      };

      var dateInt = Str(r, "date");
      if (!ImportDateParser.TryParse(dateInt, ImportDateOrder.YearFirst, out var date))
        row.Error = "The date could not be read.";
      else
        row.Date = date;

      var payeeId = payeeCol != null ? Str(r, payeeCol) : string.Empty;
      if (payeeId.Length == 0 && parent != null && payeeCol != null) payeeId = Str(parent, payeeCol);
      if (payeeId.Length > 0) payeeId = ResolvePayeeId(payeeId);
      var payee = payeeId.Length > 0 ? payeeNames.GetValueOrDefault(payeeId, string.Empty) : string.Empty;
      row.Payee = payee.Length > 0 ? payee : Str(r, "imported_description");
      if (row.Payee.Length == 0) row.Payee = "Unknown";

      if (payeeId.Length > 0 && payeeTransferAccount.TryGetValue(payeeId, out var transferAcct)
          && accountIdToKey.ContainsKey(transferAcct))
      {
        row.IsTransfer = true;
        row.TransferAccountName = parsed.Accounts.First(a => a.Key == transferAcct).Name;
      }

      var notes = Str(r, "notes");
      if (notes.Length == 0 && parent != null) notes = Str(parent, "notes");
      row.Memo = notes.Length > 0 ? notes : null;
      var imported = Str(r, "imported_description");
      row.OriginalDescription = imported.Length > 0 ? imported : null;

      if (Int(r, "starting_balance_flag") == 1)
      {
        row.IsStartingBalance = true;
        row.Memo = row.Memo != null ? $"Starting balance. {row.Memo}" : "Starting balance";
      }

      var catId = catCol != null ? Str(r, catCol) : string.Empty;
      var category = row.IsTransfer ? null : ResolveCategory(catId);
      if (category != null && category.Name.Length > 0)
      {
        row.CategoryGroup = category.Group;
        row.CategoryName = category.Name;
      }

      if (row.Error == null)
      {
        var occKey = $"{row.AccountKey}|{row.Date:yyyyMMdd}|{row.Amount}|{row.Payee.ToLowerInvariant()}";
        row.Occurrence = occurrences.GetValueOrDefault(occKey);
        occurrences[occKey] = row.Occurrence + 1;
      }
      parsed.Rows.Add(row);
    }

    if (unknownAccountRows > 0)
      parsed.Warnings.Add($"{unknownAccountRows} transactions belong to accounts that were deleted in Actual and were skipped.");
    if (splitChildren > 0)
      parsed.Warnings.Add($"{splitChildren} rows are parts of split transactions. They were imported as separate rows, each with its own category.");

    foreach (var cat in parsed.Rows.Where(r => r.CategoryName != null)
               .Select(r => (r.CategoryGroup ?? string.Empty, r.CategoryName!))
               .Distinct())
    {
      var info = categoryInfo.Values.FirstOrDefault(c => c.Group == cat.Item1 && c.Name == cat.Item2);
      parsed.Categories.Add(new ParsedImportCategory { Group = cat.Item1, Name = cat.Item2, IsIncome = info?.IsIncome ?? false });
    }

    ReadBudgets(db, parsed, categoryInfo, categoryMerge);
  }

  private static void ReadBudgets(
    SqliteConnection db,
    ParsedImport parsed,
    Dictionary<string, ParsedImportCategory> categoryInfo,
    Dictionary<string, string> categoryMerge)
  {
    // Actual stores envelope budgets in zero_budgets and tracking budgets in reflect_budgets.
    // Prefer the envelope table, fall back to the other.
    var table = new[] { "zero_budgets", "reflect_budgets" }.FirstOrDefault(t => TableExists(db, t) && RowCount(db, t) > 0);
    if (table == null) return;

    var cols = Columns(db, table);
    var catCol = Pick(cols, "category_id", "category");
    if (catCol == null) return;

    foreach (var row in Query(db, table, cols, "month", "amount", catCol))
    {
      var catId = Str(row, catCol);
      if (categoryMerge.TryGetValue(catId, out var target)) catId = target;
      if (!categoryInfo.TryGetValue(catId, out var category)) continue;

      var monthInt = Int(row, "month");
      if (monthInt > 999999) monthInt /= 100; // YYYYMMDD in some versions
      var year = (int)(monthInt / 100);
      var month = (int)(monthInt % 100);
      if (year < 1900 || month < 1 || month > 12) continue;

      var amount = Int(row, "amount") / 100m;
      if (amount == 0) continue;
      parsed.Budgets.Add(new ParsedBudgetEntry
      {
        Month = $"{year:0000}-{month:00}",
        Group = category.Group,
        Category = category.Name,
        Budgeted = amount
      });
    }
  }

  private static string GuessAccountType(string name, string type, string subtype, bool offBudget)
  {
    var t = $"{type} {subtype}".ToLowerInvariant();
    if (t.Contains("credit")) return "CreditCard";
    if (t.Contains("saving")) return "Savings";
    if (t.Contains("checking")) return "Checking";
    var guess = ImportAccountTypes.GuessFromName(name);
    if (guess != "Checking") return guess;
    return offBudget ? "Other" : "Checking";
  }

  // ---- SQLite helpers -------------------------------------------------------------------

  private static bool TableExists(SqliteConnection db, string table)
  {
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE name = $n AND type IN ('table','view') LIMIT 1";
    cmd.Parameters.AddWithValue("$n", table);
    return cmd.ExecuteScalar() != null;
  }

  private static long RowCount(SqliteConnection db, string table)
  {
    using var cmd = db.CreateCommand();
    cmd.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
    return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static HashSet<string> Columns(SqliteConnection db, string table)
  {
    var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    using var cmd = db.CreateCommand();
    cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
    using var reader = cmd.ExecuteReader();
    while (reader.Read()) cols.Add(reader.GetString(1));
    return cols;
  }

  private static string? Pick(HashSet<string> cols, params string[] candidates) =>
    candidates.FirstOrDefault(c => cols.Contains(c));

  /// <summary>Selects only the wanted columns that exist, so an older or newer schema still reads.</summary>
  private static IEnumerable<Dictionary<string, string>> Query(SqliteConnection db, string table, HashSet<string> available, params string[] wanted)
  {
    var cols = wanted.Where(available.Contains).Distinct().ToList();
    if (cols.Count == 0) yield break;

    using var cmd = db.CreateCommand();
    cmd.CommandText = $"SELECT {string.Join(", ", cols.Select(c => $"\"{c}\""))} FROM \"{table}\"";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      for (var i = 0; i < cols.Count; i++)
        row[cols[i]] = reader.IsDBNull(i)
          ? string.Empty
          : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
      yield return row;
    }
  }

  private static Dictionary<string, string> MappingTable(SqliteConnection db, string table)
  {
    var map = new Dictionary<string, string>();
    if (!TableExists(db, table)) return map;
    var cols = Columns(db, table);
    if (!cols.Contains("id") || !cols.Contains("targetId")) return map;
    foreach (var row in Query(db, table, cols, "id", "targetId"))
    {
      var id = Str(row, "id");
      var target = Str(row, "targetId");
      if (id.Length > 0 && target.Length > 0 && id != target) map[id] = target;
    }
    return map;
  }

  private static string Str(Dictionary<string, string> row, string key) =>
    row.TryGetValue(key, out var v) ? v.Trim() : string.Empty;

  private static long Int(Dictionary<string, string> row, string key) =>
    long.TryParse(Str(row, key), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;

  private static bool IsTombstone(Dictionary<string, string> row) => Int(row, "tombstone") == 1;
}
