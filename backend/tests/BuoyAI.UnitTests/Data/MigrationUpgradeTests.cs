using BuoyAI.Infrastructure.Data;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace BuoyAI.UnitTests.Data;

/// <summary>
/// Upgrade-path checks against a real Postgres, run by CI (.github/workflows/migration-upgrade.yml)
/// and locally by scripts/dev/migration-upgrade-check.sh. Skipped without
/// BUOYAI_MIGRATION_TEST_CONNECTION.
/// </summary>
public class MigrationUpgradeTests
{
  private static readonly int UpScriptCount = typeof(SqlMigrationRunner).Assembly
    .GetManifestResourceNames().Count(n => n.EndsWith(".up.sql", StringComparison.OrdinalIgnoreCase));

  private static int NumberOf(string resourceName)
  {
    // Resource names look like "<namespace>.Migrations.Sql.0013_x.up.sql".
    var file = resourceName[..^".up.sql".Length];
    var tail = file[(file.LastIndexOf('.') + 1)..];
    return int.Parse(tail[..4]);
  }

  [PostgresFact]
  public void EmptyDatabase_UpgradesToHead_AndRerunIsANoOp()
  {
    using var db = new ScratchDatabase();

    SqlMigrationRunner.Run(db.ConnectionString);
    db.JournalCount().Should().Be(UpScriptCount);

    var act = () => SqlMigrationRunner.Run(db.ConnectionString);
    act.Should().NotThrow("a second run must find nothing pending");
    db.JournalCount().Should().Be(UpScriptCount);
  }

  [PostgresFact]
  public void OlderSchema_UpgradesToHead()
  {
    using var db = new ScratchDatabase();

    // An older release's state: only the scripts that existed at that point.
    SqlMigrationRunner.Run(db.ConnectionString, null, null, null, name => NumberOf(name) <= 30, null);
    var older = db.JournalCount();
    older.Should().BeGreaterThan(0).And.BeLessThan(UpScriptCount);

    SqlMigrationRunner.Run(db.ConnectionString);

    db.JournalCount().Should().Be(UpScriptCount);
  }

  [PostgresFact]
  public void EfManagedDatabase_IsBaselined_ThenUpgradedToHead()
  {
    using var db = new ScratchDatabase();

    // The hosted cutover path: a schema built by the old EF migrations (baseline-equivalent),
    // with __EFMigrationsHistory and no schemaversions journal.
    SqlMigrationRunner.Run(db.ConnectionString, null, null, null, name => NumberOf(name) == 0, null);
    // The baseline already creates __EFMigrationsHistory; dropping the journal recreates the old state.
    db.Execute("DROP TABLE schemaversions;");

    SqlMigrationRunner.Run(db.ConnectionString);

    db.JournalCount().Should().Be(UpScriptCount);
    db.Scalar("SELECT count(*) FROM schemaversions WHERE scriptname LIKE '%0000_baseline.up.sql'").Should().Be(1L);
  }

  [PostgresFact]
  public void EveryUpScript_IsJournaledUnderItsResourceName()
  {
    using var db = new ScratchDatabase();
    SqlMigrationRunner.Run(db.ConnectionString);

    var journaled = new List<string>();
    using (var c = new NpgsqlConnection(db.ConnectionString))
    {
      c.Open();
      using var cmd = new NpgsqlCommand("SELECT scriptname FROM schemaversions", c);
      using var r = cmd.ExecuteReader();
      while (r.Read()) journaled.Add(r.GetString(0));
    }

    var resources = typeof(SqlMigrationRunner).Assembly.GetManifestResourceNames()
      .Where(n => n.EndsWith(".up.sql", StringComparison.OrdinalIgnoreCase));
    journaled.Should().BeEquivalentTo(resources);
  }

  [PostgresFact]
  public void RealPgDump_TakesAndVerifiesABackup_BeforeUpgrading()
  {
    var required = Environment.GetEnvironmentVariable("BUOYAI_REQUIRE_PG_TOOLS") == "true";
    if (!PgToolsAvailable())
    {
      required.Should().BeFalse("CI installs pg_dump and pg_restore; they must be on PATH there");
      return; // local run without the PostgreSQL client tools
    }

    using var db = new ScratchDatabase();
    var dir = Path.Combine(Path.GetTempPath(), "bm06-realdump-" + Guid.NewGuid().ToString("N"));
    try
    {
      SqlMigrationRunner.Run(db.ConnectionString, null, null, null, name => NumberOf(name) <= 30, null);
      var settings = new MigrationSettings { BackupBeforeMigrate = true, BackupDirectory = dir };

      SqlMigrationRunner.Run(db.ConnectionString, null, settings);

      db.JournalCount().Should().Be(UpScriptCount);
      var dumps = Directory.GetFiles(dir, "*.dump");
      dumps.Should().ContainSingle();
      new FileInfo(dumps[0]).Length.Should().BeGreaterThan(0);
    }
    finally
    {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  private static bool PgToolsAvailable()
  {
    try
    {
      var runner = new SystemProcessRunner();
      var env = new Dictionary<string, string>();
      return runner.Run(new ProcessRunRequest("pg_dump", new[] { "--version" }, env)).ExitCode == 0
        && runner.Run(new ProcessRunRequest("pg_restore", new[] { "--version" }, env)).ExitCode == 0;
    }
    catch (MigrationBackupException)
    {
      return false;
    }
  }
}

/// <summary>0058: accounts of a removed bank connection stop counting.</summary>
public class DeactivateOrphanedAccountsMigrationTests
{
  [PostgresFact]
  public void Migration0058_DeactivatesAccountsOfInactiveConnections_AndNothingElse()
  {
    using var db = new ScratchDatabase();
    SqlMigrationRunner.Run(db.ConnectionString, null, null, null, name => int.Parse(name[..name.LastIndexOf(".up.sql", StringComparison.Ordinal)].Split('.').Last()[..4]) < 58, null);

    // One batch: the replica role (FKs off, so no user rows are needed) lasts only for this session.
    db.Execute(@"
      SET session_replication_role = replica;
      INSERT INTO public.""PlaidConnections"" (""Id"",""UserId"",""AccessToken"",""ItemId"",""InstitutionId"",""InstitutionName"",""IsActive"",""ImportStatus"",""ImportProgress"",""Status"",""RequiresUserAction"",""ConsecutiveFailures"")
        VALUES (1,1,'t','i1','ins1','Gone',false,0,0,0,false,0), (2,1,'t','i2','ins2','Live',true,0,0,0,false,0);
      INSERT INTO public.""Accounts"" (""Id"",""UserId"",""PlaidConnectionId"",""PlaidAccountId"",""Name"",""OfficialName"",""Type"",""Subtype"",""CurrentBalance"",""AvailableBalance"")
        VALUES (1,1,1,'a1','Orphan','Orphan',0,'checking',100,100),
               (2,1,2,'a2','Live','Live',0,'checking',100,100),
               (3,1,NULL,'a3','Imported','Imported',0,'checking',100,100);");

    SqlMigrationRunner.Run(db.ConnectionString);

    db.Scalar(@"SELECT ""IsActive"" FROM public.""Accounts"" WHERE ""Id"" = 1").Should().Be(false);
    db.Scalar(@"SELECT ""IsActive"" FROM public.""Accounts"" WHERE ""Id"" = 2").Should().Be(true);
    db.Scalar(@"SELECT ""IsActive"" FROM public.""Accounts"" WHERE ""Id"" = 3").Should().Be(true);
    db.Scalar(@"SELECT count(*) FROM public.""Accounts""").Should().Be(3L, "rows are kept");
  }
}
