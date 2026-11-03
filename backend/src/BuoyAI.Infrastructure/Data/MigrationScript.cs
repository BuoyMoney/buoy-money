using System.Text;
using System.Text.RegularExpressions;
using DbUp.Engine;
using DbUp.Engine.Output;
using DbUp.Engine.Transactions;
using DbUp.Postgresql;

namespace BuoyAI.Infrastructure.Data;

/// <summary>How the runner executes one migration script.</summary>
public enum MigrationTransactionMode
{
  /// <summary>The runner wraps the script in BEGIN/COMMIT. Default for every new script.</summary>
  Wrapped,

  /// <summary>The script carries the <c>-- buoy:no-transaction</c> marker and runs as written
  /// (CREATE INDEX CONCURRENTLY, VACUUM and similar statements that cannot run in a transaction).</summary>
  OptOut,

  /// <summary>Legacy script that already opens its own BEGIN/COMMIT. Run as written so the
  /// transaction is not nested. Existing scripts keep their SQL and journal names unchanged.</summary>
  SelfManaged,
}

/// <summary>
/// Classifies and wraps migration scripts. Pure text functions: no database access, so the rules
/// are unit-testable and shared with the migration lint.
/// </summary>
public static class MigrationScript
{
  /// <summary>Put this on its own line (as a SQL comment) to run the script outside a transaction.</summary>
  public const string NoTransactionMarker = "-- buoy:no-transaction";

  /// <summary>Put this on its own line, with a reason after the colon, to allow a destructive statement.</summary>
  public const string ReviewedDestructiveMarker = "-- buoy:reviewed-destructive:";

  private static readonly Regex NoTransactionLine = new(
    @"^[ \t]*--[ \t]*buoy:no-transaction\b", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

  // A top-level transaction start on one line: BEGIN; / BEGIN TRANSACTION; / START TRANSACTION;
  private static readonly Regex TxnStart = new(
    @"^[ \t]*(BEGIN|START[ \t]+TRANSACTION)\b[^;\r\n]*;", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

  private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
  private static readonly Regex LineComment = new(@"--[^\r\n]*", RegexOptions.Compiled);
  private static readonly Regex DollarQuoted = new(
    @"\$\$.*?\$\$|\$(?<tag>[A-Za-z_][A-Za-z0-9_]*)\$.*?\$\k<tag>\$", RegexOptions.Singleline | RegexOptions.Compiled);

  public static bool HasNoTransactionMarker(string sql) => NoTransactionLine.IsMatch(sql);

  public static MigrationTransactionMode Classify(string sql)
  {
    if (HasNoTransactionMarker(sql))
    {
      return MigrationTransactionMode.OptOut;
    }

    // Look only at top-level SQL: a plpgsql "BEGIN" inside a DO $$ body is not a transaction start.
    var topLevel = DollarQuoted.Replace(StripComments(sql), "''");
    return TxnStart.IsMatch(topLevel) ? MigrationTransactionMode.SelfManaged : MigrationTransactionMode.Wrapped;
  }

  /// <summary>Removes <c>--</c> and block comments. Not a SQL parser: good enough for linting migrations.</summary>
  public static string StripComments(string sql) => LineComment.Replace(BlockComment.Replace(sql, " "), string.Empty);

  /// <summary>Strips comments and dollar-quoted bodies, leaving only top-level statements.</summary>
  public static string TopLevelSql(string sql) => DollarQuoted.Replace(StripComments(sql), "''");

  /// <summary>Wraps a script so it commits or rolls back as a unit. The lone ';' guards a final
  /// statement that has no terminator.</summary>
  public static string Wrap(string sql) =>
    new StringBuilder("BEGIN;\n").Append(sql).Append("\n;\nCOMMIT;\n").ToString();

  public static string Prepare(string sql) =>
    Classify(sql) == MigrationTransactionMode.Wrapped ? Wrap(sql) : sql;

  /// <summary>
  /// Wraps a script so its statements and its journal row commit in ONE transaction. A crash
  /// between "schema changed" and "journal written" would otherwise leave the script pending
  /// and re-run it on the next boot. The insert is idempotent, so the journal write DbUp does
  /// afterwards (see <see cref="IdempotentJournal"/>) does not add a second row.
  /// </summary>
  public static string WrapWithJournal(string sql, string scriptName, string journalTable) =>
    new StringBuilder("BEGIN;\n").Append(sql).Append("\n;\n")
      .Append(IdempotentInsertSql(journalTable, "'" + scriptName.Replace("'", "''") + "'", "now()"))
      .Append(";\nCOMMIT;\n").ToString();

  internal static string IdempotentInsertSql(string journalTable, string nameExpr, string appliedExpr) =>
    $"INSERT INTO {journalTable} (scriptname, applied) SELECT {nameExpr}, {appliedExpr} " +
    $"WHERE NOT EXISTS (SELECT 1 FROM {journalTable} WHERE scriptname = {nameExpr})";

  /// <summary>Applies <see cref="Prepare"/> and, for wrapped scripts, journals inside the transaction.</summary>
  public static SqlScript Prepare(SqlScript script, string journalTable) =>
    Classify(script.Contents) == MigrationTransactionMode.Wrapped
      ? new SqlScript(script.Name, WrapWithJournal(script.Contents, script.Name, journalTable), script.SqlScriptOptions)
      : script;
}

/// <summary>DbUp's Postgres journal with a duplicate-safe insert: a script that journaled itself
/// inside its own transaction is not journaled a second time.</summary>
internal sealed class IdempotentJournal : PostgresqlTableJournal
{
  private readonly string _qualifiedTable;

  public IdempotentJournal(Func<IConnectionManager> connectionManager, Func<IUpgradeLog> logger, string schema, string table)
    : base(connectionManager, logger, schema, table)
  {
    _qualifiedTable = $"\"{schema}\".\"{table}\"";
  }

  protected override string GetInsertJournalEntrySql(string scriptName, string applied) =>
    MigrationScript.IdempotentInsertSql(_qualifiedTable, scriptName, applied);
}

