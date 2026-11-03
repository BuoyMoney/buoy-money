using System.Reflection;
using DbUp;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BuoyAI.Infrastructure.Data;

/// <summary>
/// SQL-first migration runner (#533). Applies hand-written <c>*.up.sql</c> scripts that are
/// embedded in this assembly, in filename order, journaling each to the <c>schemaversions</c>
/// table. Replaces EF Core's <c>MigrateAsync()</c> — no model snapshot, no <c>.Designer.cs</c>,
/// no silent-skip; each migration is a plain, reviewable SQL file.
///
/// Database state handling:
/// <list type="bullet">
///   <item><b>Fresh DB</b> (no <c>schemaversions</c>, no <c>__EFMigrationsHistory</c>) — runs
///   <c>0000_baseline</c> (full schema) then every later migration.</item>
///   <item><b>Existing EF-managed DB</b> (has <c>__EFMigrationsHistory</c>, no
///   <c>schemaversions</c>) — the schema is already current, so we <b>pre-seed</b> the journal
///   with <c>0000_baseline</c> (mark applied, do NOT re-run the full DDL) and then apply only
///   genuinely-new migrations. This is the prod/staging cutover path.</item>
/// </list>
/// Forward (<c>*.up.sql</c>) scripts are the only ones DbUp runs. Paired <c>*.down.sql</c> files
/// are committed beside them for manual, reviewed rollback and are intentionally not embedded.
/// </summary>
public static class SqlMigrationRunner
{
    private const string BaselineSuffix = "0000_baseline.up.sql";

    // Session-level advisory-lock key: serializes migration runs so two app instances booting
    // at once don't race on the (DbUp-non-transactional) DDL or both pass PreSeedBaseline's
    // NOT EXISTS guard and double-insert the journal row. Arbitrary fixed constant ("BUOYAI"+533).
    private const long MigrationAdvisoryLockKey = 0x4255_4F59_4149_0533;

    public static void Run(string connectionString, ILogger? logger = null, MigrationSettings? settings = null) =>
        Run(connectionString, logger, settings, processRunner: null, scriptFilter: null, scriptsOverride: null);

    // processRunner, scriptFilter and scriptsOverride are test seams (UnitTests has InternalsVisibleTo).
    internal static void Run(
        string connectionString,
        ILogger? logger,
        MigrationSettings? settings,
        IProcessRunner? processRunner,
        Func<string, bool>? scriptFilter,
        IReadOnlyList<DbUp.Engine.SqlScript>? scriptsOverride)
    {
        var assembly = typeof(SqlMigrationRunner).Assembly;

        // Create the database only when it genuinely doesn't exist (fresh local/dev). EnsureDatabase
        // connects to the "postgres" maintenance DB, which the prod app user often can't reach — so
        // we take that path ONLY on the specific "database does not exist" (3D000) error, never on a
        // transient/auth/network failure (those must surface, not be masked as "create the DB").
        if (DatabaseDoesNotExist(connectionString, logger))
        {
            logger?.LogInformation("Target database does not exist; creating it (fresh environment).");
            EnsureDatabase.For.PostgresqlDatabase(connectionString);
        }

        // Serialize concurrent boots: hold a session-level advisory lock for the whole migration.
        // A second instance blocks on pg_advisory_lock until the first finishes, then finds the
        // journal already populated and applies nothing.
        using var lockConnection = new NpgsqlConnection(connectionString);
        lockConnection.Open();
        SetAdvisoryLock(lockConnection, acquire: true);
        try
        {
            var databaseHasState = DatabaseHasState(connectionString);
            if (NeedsBaselinePreSeed(connectionString, logger))
            {
                PreSeedBaseline(connectionString, assembly, logger);
            }

            // Transactional by default: DbUp itself runs without a transaction, and each wrapped
            // script is rewritten to BEGIN; <script>; <journal insert>; COMMIT; so the schema change
            // and its journal row are one atomic unit (a crash can never leave the change applied but
            // unjournaled). A script opts out with the "-- buoy:no-transaction" marker (CREATE INDEX
            // CONCURRENTLY and the like); legacy scripts that open their own BEGIN run as written.
            // See MigrationScript. Journal names are unchanged, so nothing re-runs.
            var scripts = (scriptsOverride ?? LoadEmbeddedScripts(assembly, scriptFilter))
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .Select(s => MigrationScript.Prepare(s, JournalTable))
                .ToList();
            var builder = DeployChanges.To
                .PostgresqlDatabase(connectionString)
                .JournalToPostgresqlTable("public", "schemaversions");
            // Swap in the duplicate-safe journal (each wrapped script already journals itself).
            builder.Configure(c => c.Journal = new IdempotentJournal(() => c.ConnectionManager, () => c.Log, "public", "schemaversions"));
            var upgrader = builder
                .WithScripts(scripts)
                .WithoutTransaction()
                // pg_dump / EF SQL uses `$tag$` dollar-quoting; disable DbUp's `$var$` substitution
                // so it isn't misread as an undefined variable.
                .WithVariablesDisabled()
                .LogToConsole()
                .Build();

            var pending = upgrader.GetScriptsToExecute();
            logger?.LogInformation("SQL migrations pending: {Count} [{Scripts}]",
                pending.Count, string.Join(", ", pending.Select(s => s.Name)));

            // Self-host safety net: dump and verify the database before touching an existing one.
            // Off by default, so hosted behaviour is unchanged (its blue-green deploy covers this).
            if (settings is { BackupBeforeMigrate: true } && databaseHasState && pending.Count > 0)
            {
                TakeBackupOrRefuse(connectionString, settings, processRunner ?? new SystemProcessRunner(), logger);
            }

            var result = upgrader.PerformUpgrade();
            if (!result.Successful)
            {
                logger?.LogCritical(result.Error, "SQL migration FAILED on script {Script}", result.ErrorScript?.Name);
                throw new InvalidOperationException(
                    $"SQL migration failed on '{result.ErrorScript?.Name}': {result.Error?.Message}", result.Error);
            }

            logger?.LogInformation("SQL migrations applied successfully ({Count} executed).",
                result.Scripts.Count());
        }
        finally
        {
            SetAdvisoryLock(lockConnection, acquire: false);
        }
    }

    private const string JournalTable = "\"public\".\"schemaversions\"";

    private static List<DbUp.Engine.SqlScript> LoadEmbeddedScripts(Assembly assembly, Func<string, bool>? scriptFilter) =>
        assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".up.sql", StringComparison.OrdinalIgnoreCase) && (scriptFilter?.Invoke(n) ?? true))
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return new DbUp.Engine.SqlScript(n, reader.ReadToEnd());
            })
            .ToList();

    private static void TakeBackupOrRefuse(
        string connectionString, MigrationSettings settings, IProcessRunner runner, ILogger? logger)
    {
        try
        {
            MigrationBackup.Create(connectionString, settings, runner, logger);
        }
        catch (MigrationBackupException ex)
        {
            if (settings.AllowMigrateWithoutBackup)
            {
                logger?.LogCritical(ex,
                    "Pre-migration backup FAILED; migrating anyway because Migrations:AllowMigrateWithoutBackup is set.");
                return;
            }

            logger?.LogCritical(ex, "Pre-migration backup FAILED; refusing to migrate.");
            throw new InvalidOperationException(
                "Refusing to apply database migrations: the pre-migration backup failed (" + ex.Message + "). " +
                "Fix the backup, or set Migrations:AllowMigrateWithoutBackup=true to migrate without one.", ex);
        }
    }

    private static bool DatabaseHasState(string connectionString)
    {
        using var c = new NpgsqlConnection(connectionString);
        c.Open();
        return TableExists(c, "schemaversions") || TableExists(c, "__EFMigrationsHistory");
    }

    private static void SetAdvisoryLock(NpgsqlConnection openConnection, bool acquire)
    {
        using var cmd = openConnection.CreateCommand();
        cmd.CommandText = acquire
            ? "SELECT pg_advisory_lock(@key)"
            : "SELECT pg_advisory_unlock(@key)";
        cmd.Parameters.AddWithValue("key", MigrationAdvisoryLockKey);
        cmd.ExecuteNonQuery();
    }

    private static bool DatabaseDoesNotExist(string connectionString, ILogger? logger)
    {
        try
        {
            using var c = new NpgsqlConnection(connectionString);
            c.Open();
            return false; // reachable → the database exists
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            // 3D000 = the target database itself is missing — the one case EnsureDatabase handles.
            logger?.LogInformation("Target database does not exist yet (3D000); it will be created.");
            return true;
        }
        // Any other failure (auth, network, server starting) propagates: it is NOT a missing
        // database, and routing it to EnsureDatabase would only produce a misleading error.
    }

    private static bool NeedsBaselinePreSeed(string connectionString, ILogger? logger)
    {
        using var c = new NpgsqlConnection(connectionString);
        c.Open();

        if (TableExists(c, "schemaversions"))
        {
            return false; // DbUp already owns this database
        }

        if (TableExists(c, "__EFMigrationsHistory"))
        {
            logger?.LogWarning(
                "Existing EF-managed database detected (has __EFMigrationsHistory, no schemaversions). " +
                "Baselining: 0000_baseline will be marked already-applied, not re-run.");
            return true;
        }

        return false; // truly fresh DB → DbUp runs the baseline itself
    }

    private static bool TableExists(NpgsqlConnection c, string unquotedTableName)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT to_regclass(@qualified) IS NOT NULL";
        cmd.Parameters.AddWithValue("qualified", $"public.\"{unquotedTableName}\"");
        return cmd.ExecuteScalar() is bool exists && exists;
    }

    private static void PreSeedBaseline(string connectionString, Assembly assembly, ILogger? logger)
    {
        // Use the exact embedded-resource name DbUp would journal, so it recognises the
        // baseline as applied and skips it.
        var baselineName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(BaselineSuffix, StringComparison.OrdinalIgnoreCase));

        using var c = new NpgsqlConnection(connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        // Mirror DbUp's PostgresqlTableJournal schema so DbUp reuses this table as-is.
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS "public"."schemaversions" (
                "schemaversionsid" SERIAL NOT NULL,
                "scriptname" CHARACTER VARYING(255) NOT NULL,
                "applied" TIMESTAMP NOT NULL,
                CONSTRAINT "PK_schemaversions_Id" PRIMARY KEY ("schemaversionsid")
            );
            INSERT INTO "public"."schemaversions" ("scriptname", "applied")
            SELECT @name, now()
            WHERE NOT EXISTS (
                SELECT 1 FROM "public"."schemaversions" WHERE "scriptname" = @name);
            """;
        cmd.Parameters.AddWithValue("name", baselineName);
        cmd.ExecuteNonQuery();
        logger?.LogWarning("Pre-seeded schemaversions journal with baseline '{Baseline}'.", baselineName);
    }
}
