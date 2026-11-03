using System.Text.RegularExpressions;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.UnitTests.Data;

public sealed record MigrationFile(string Name, string Sql);

/// <summary>
/// Rules every SQL migration must pass (see Migrations/Sql/README.md). Pure functions over
/// (name, sql) pairs so the rules are tested with fixtures and then run over the real folder.
/// </summary>
public static class MigrationLint
{
  /// <summary>Scripts numbered above this are "new" and held to the strict rules. 0047 was the
  /// newest script when the lint was introduced; the next number to use is 0049 (0048 is the pgvector embeddings table).</summary>
  public const int LegacyCutoff = 47;

  /// <summary>
  /// Duplicate numbers that already shipped. Parallel branches landed with the same prefix before
  /// the lint existed. DbUp journals by full script name and orders by name, so these run in a
  /// stable order on every database and renaming them would make hosted re-run them. They stay as
  /// they are; no new duplicate is allowed.
  /// </summary>
  public static readonly IReadOnlyDictionary<string, string[]> GrandfatheredDuplicates =
    new Dictionary<string, string[]>
    {
      ["0013"] = new[] { "0013_add_unique_active_subscription_per_user.up.sql", "0013_budget_category_integrity.up.sql" },
      ["0014"] = new[] { "0014_add_import_attempts_to_plaid_connections.up.sql", "0014_transfer_savings_goal_fk_setnull.up.sql" },
      ["0015"] = new[] { "0015_add_transfer_reviewed_at_to_transactions.up.sql", "0015_snapshot_category_fk_nullable.up.sql" },
      ["0021"] = new[] { "0021_add_push_notification_tables.up.sql", "0021_backfill_linked_personal_budget_categories.up.sql" },
    };

  /// <summary>
  /// Existing scripts that contain a destructive statement and are already applied everywhere.
  /// They predate the reviewed marker and cannot be edited (the SQL is frozen). New scripts need
  /// the marker instead of an entry here.
  /// </summary>
  public static readonly IReadOnlySet<string> GrandfatheredDestructive = new HashSet<string>(StringComparer.Ordinal)
  {
    // Drops a table nothing reads any more (already applied on every database).
    "0002_drop_savings_achievements.up.sql",
    // Drops a table nothing reads any more (already applied on every database).
    "0007_drop_devtimeline_summary.up.sql",
    // Hotfix that converts four timestamp columns to timestamptz (lossless, guarded, applied on hosted).
    "0034_timestamptz_alignment.up.sql",
  };

  private static readonly Regex NameShape = new(@"^(?<num>\d{4})_[a-z0-9_]+\.up\.sql$", RegexOptions.Compiled);
  private static readonly Regex ReviewedMarker = new(
    @"^[ \t]*--[ \t]*buoy:reviewed-destructive:[ \t]*\S", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
  private static readonly Regex DropObject = new(@"\bDROP\s+(TABLE|COLUMN|SCHEMA)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
  private static readonly Regex Truncate = new(@"\bTRUNCATE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
  // COLUMN is optional in PostgreSQL: "ALTER TABLE t ALTER c TYPE bigint" is the same statement.
  private static readonly Regex AlterType = new(
    @"\bALTER\s+(COLUMN\s+)?(""[^""]+""|\w+)\s+(SET\s+DATA\s+)?TYPE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
  // "ALTER TABLE t DROP c" drops a column too. Excludes the non-destructive DROP forms
  // (CONSTRAINT, DEFAULT, NOT NULL, IDENTITY, EXPRESSION).
  private static readonly Regex AlterTable = new(@"\bALTER\s+TABLE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
  private static readonly Regex DropColumnShort = new(
    @"\bDROP\s+(COLUMN\s+)?(IF\s+EXISTS\s+)?(?!(CONSTRAINT|DEFAULT|NOT|IDENTITY|EXPRESSION)\b)(""[^""]+""|\w+)",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);
  // Anywhere in the statement, so "WITH ... DELETE FROM" is caught too.
  private static readonly Regex DeleteFrom = new(@"\bDELETE\s+FROM\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
  private static readonly Regex QuotedLiteral = new(@"'(?:[^']|'')*'", RegexOptions.Compiled);
  private static readonly Regex Where = new(@"\bWHERE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
  private static readonly Regex NeedsNoTransaction = new(
    @"\b(CREATE\s+(UNIQUE\s+)?INDEX\s+CONCURRENTLY|DROP\s+INDEX\s+CONCURRENTLY|REINDEX\s+[^;]*CONCURRENTLY|VACUUM|CREATE\s+DATABASE|DROP\s+DATABASE|ALTER\s+SYSTEM)\b",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);
  private static readonly Regex TxnControl = new(
    @"^[ \t]*(COMMIT|ROLLBACK|END|BEGIN|START\s+TRANSACTION)\b[^;\r\n]*;", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

  public static IReadOnlyList<string> Check(IReadOnlyCollection<MigrationFile> ups, IReadOnlyCollection<string> downNames)
  {
    var problems = new List<string>();
    var downs = new HashSet<string>(downNames, StringComparer.Ordinal);

    foreach (var f in ups.OrderBy(x => x.Name, StringComparer.Ordinal))
    {
      var m = NameShape.Match(f.Name);
      if (!m.Success)
      {
        problems.Add($"{f.Name}: name must look like NNNN_snake_case.up.sql");
        continue;
      }
      var number = int.Parse(m.Groups["num"].Value);
      var isNew = number > LegacyCutoff;

      var downName = f.Name[..^".up.sql".Length] + ".down.sql";
      if (!downs.Contains(downName))
      {
        problems.Add($"{f.Name}: missing down script {downName}");
      }

      CheckDestructive(f, problems);
      CheckTransactionRules(f, isNew, problems);
    }

    foreach (var g in ups.GroupBy(x => x.Name[..Math.Min(4, x.Name.Length)]).Where(g => g.Count() > 1))
    {
      var names = g.Select(x => x.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
      var allowed = GrandfatheredDuplicates.TryGetValue(g.Key, out var set) && names.All(n => set.Contains(n));
      if (!allowed)
      {
        problems.Add($"duplicate migration number {g.Key}: {string.Join(", ", names)} (only the grandfathered duplicates are allowed)");
      }
    }

    return problems;
  }

  private static void CheckDestructive(MigrationFile f, List<string> problems)
  {
    if (GrandfatheredDestructive.Contains(f.Name) || ReviewedMarker.IsMatch(f.Sql))
    {
      return;
    }

    var code = MigrationScript.StripComments(f.Sql);
    var hits = new List<string>();
    if (DropObject.Match(code) is { Success: true } d) hits.Add(d.Value.ToUpperInvariant());
    else if (code.Split(';').Any(stmt => AlterTable.IsMatch(stmt) && DropColumnShort.IsMatch(stmt))) hits.Add("DROP COLUMN");
    if (Truncate.IsMatch(code)) hits.Add("TRUNCATE");
    if (AlterType.IsMatch(code)) hits.Add("ALTER COLUMN ... TYPE");
    if (code.Split(';').Any(stmt => DeleteFrom.IsMatch(stmt) && !HasOuterWhere(stmt))) hits.Add("DELETE without WHERE");

    if (hits.Count > 0)
    {
      problems.Add(
        $"{f.Name}: destructive statement ({string.Join(", ", hits)}). Blue-green deploys need additive migrations; " +
        $"if this is deliberate and the old version no longer uses it, add a line '{MigrationScript.ReviewedDestructiveMarker} <reason>'.");
    }
  }

  /// <summary>
  /// True only when a WHERE appears outside every parenthesis, so a WHERE inside a subquery or CTE
  /// (USING (SELECT 1 WHERE true)) does not count. Conservative: when the outer predicate cannot be
  /// proven, the statement is flagged and needs the reviewed marker.
  /// </summary>
  private static bool HasOuterWhere(string stmt)
  {
    var text = QuotedLiteral.Replace(stmt, "''");
    var depth = 0;
    var outer = new System.Text.StringBuilder();
    foreach (var c in text)
    {
      if (c == '(') depth++;
      else if (c == ')') depth = Math.Max(0, depth - 1);
      else if (depth == 0) outer.Append(c);
    }
    return Where.IsMatch(outer.ToString());
  }

  private static void CheckTransactionRules(MigrationFile f, bool isNew, List<string> problems)
  {
    var mode = MigrationScript.Classify(f.Sql);
    var code = MigrationScript.StripComments(f.Sql);

    if (NeedsNoTransaction.IsMatch(code) && mode != MigrationTransactionMode.OptOut)
    {
      problems.Add($"{f.Name}: uses a statement that cannot run in a transaction; add a line '{MigrationScript.NoTransactionMarker}'.");
    }

    if (!isNew)
    {
      return;
    }

    if (mode == MigrationTransactionMode.SelfManaged)
    {
      problems.Add($"{f.Name}: new scripts must not open their own BEGIN; the runner wraps them in a transaction.");
    }
    else if (TxnControl.IsMatch(MigrationScript.TopLevelSql(f.Sql)))
    {
      // Wrapped: a stray COMMIT/ROLLBACK would end the runner's transaction early.
      // OptOut: the marker already means "no transaction", so a BEGIN/COMMIT is self-managed too.
      problems.Add(mode == MigrationTransactionMode.OptOut
        ? $"{f.Name}: new scripts must not manage their own transaction, even with '{MigrationScript.NoTransactionMarker}'; remove BEGIN/COMMIT/ROLLBACK."
        : $"{f.Name}: remove COMMIT/ROLLBACK/BEGIN; the runner owns the transaction (or add '{MigrationScript.NoTransactionMarker}').");
    }
  }
}
