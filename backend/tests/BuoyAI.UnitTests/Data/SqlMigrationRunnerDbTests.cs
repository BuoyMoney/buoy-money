using BuoyAI.Infrastructure.Data;
using DbUp.Engine;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Data;

/// <summary>Runs the real runner against a throwaway Postgres. Skipped unless
/// BUOYAI_MIGRATION_TEST_CONNECTION is set (see PostgresFactAttribute).</summary>
public class SqlMigrationRunnerDbTests
{
  private static void RunScripts(ScratchDatabase db, params SqlScript[] scripts) =>
    SqlMigrationRunner.Run(db.ConnectionString, null, null, null, null, scripts);

  [PostgresFact]
  public void FailingScript_RollsBackEverythingItDid_AndIsNotJournaled()
  {
    using var db = new ScratchDatabase();

    var act = () => RunScripts(db,
      new SqlScript("0001_ok.up.sql", "CREATE TABLE \"First\" (id int);"),
      new SqlScript("0002_half.up.sql", "CREATE TABLE \"Second\" (id int);\nINSERT INTO \"Missing\" VALUES (1);"));

    act.Should().Throw<InvalidOperationException>().WithMessage("*0002_half*");
    db.TableExists("First").Should().BeTrue("the earlier script committed");
    db.TableExists("Second").Should().BeFalse("the failing script must roll back as a unit");
    db.Scalar("SELECT count(*) FROM schemaversions WHERE scriptname = '0002_half.up.sql'").Should().Be(0L);
    db.Scalar("SELECT count(*) FROM schemaversions WHERE scriptname = '0001_ok.up.sql'").Should().Be(1L);
  }

  [PostgresFact]
  public void FixedScript_AppliesCleanly_AfterAFailedRun()
  {
    using var db = new ScratchDatabase();
    var bad = () => RunScripts(db, new SqlScript("0001_x.up.sql", "CREATE TABLE \"T\" (id int);\nSELECT nope FROM nowhere;"));
    bad.Should().Throw<InvalidOperationException>();

    RunScripts(db, new SqlScript("0001_x.up.sql", "CREATE TABLE \"T\" (id int);"));

    db.TableExists("T").Should().BeTrue();
    db.JournalCount().Should().Be(1);
  }

  [PostgresFact]
  public void OptOutMarker_RunsOutsideATransaction()
  {
    using var db = new ScratchDatabase();
    db.Execute("CREATE TABLE \"Big\" (id int);");

    // CREATE INDEX CONCURRENTLY fails inside a transaction block, so this only passes if the
    // marker is honoured.
    RunScripts(db, new SqlScript(
      "0001_idx.up.sql",
      "-- buoy:no-transaction\nCREATE INDEX CONCURRENTLY IF NOT EXISTS ix_big ON \"Big\"(id);"));

    db.Scalar("SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_big'").Should().Be(1L);
  }

  [PostgresFact]
  public void WithoutMarker_ConcurrentIndex_FailsBecauseTheScriptIsWrapped()
  {
    using var db = new ScratchDatabase();
    db.Execute("CREATE TABLE \"Big\" (id int);");

    var act = () => RunScripts(db, new SqlScript(
      "0001_idx.up.sql", "CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_big ON \"Big\"(id);"));

    act.Should().Throw<InvalidOperationException>();
    db.Scalar("SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_big'").Should().Be(0L);
  }

  [PostgresFact]
  public void LegacySelfManagedScript_StillRuns_WithoutNestedTransaction()
  {
    using var db = new ScratchDatabase();

    RunScripts(db, new SqlScript("0001_legacy.up.sql", "BEGIN;\nCREATE TABLE \"L\" (id int);\nCOMMIT;"));

    db.TableExists("L").Should().BeTrue();
    db.JournalCount().Should().Be(1);
  }

  [PostgresFact]
  public void AppliedScripts_AreJournaledByName_AndNotRerun()
  {
    using var db = new ScratchDatabase();
    var scripts = new[] { new SqlScript("0001_a.up.sql", "CREATE TABLE \"A\" (id int);") };

    RunScripts(db, scripts);
    RunScripts(db, scripts); // would throw "already exists" if it ran again

    db.Scalar("SELECT scriptname FROM schemaversions").Should().Be("0001_a.up.sql");
  }
}
