using BuoyAI.Infrastructure.Data;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Data;

public class MigrationScriptTests
{
  [Fact]
  public void PlainScript_IsWrapped()
  {
    MigrationScript.Classify("ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"X\" int;")
      .Should().Be(MigrationTransactionMode.Wrapped);
  }

  [Fact]
  public void Wrap_AddsBeginAndCommit_AndGuardsMissingTerminator()
  {
    var wrapped = MigrationScript.Prepare("SELECT 1");
    wrapped.Should().StartWith("BEGIN;");
    wrapped.TrimEnd().Should().EndWith("COMMIT;");
    wrapped.Should().Contain("SELECT 1\n;\nCOMMIT;");
  }

  [Fact]
  public void NoTransactionMarker_OptsOut_AndIsNotWrapped()
  {
    var sql = "-- buoy:no-transaction (index build)\nCREATE INDEX CONCURRENTLY IF NOT EXISTS ix ON t(c);";
    MigrationScript.Classify(sql).Should().Be(MigrationTransactionMode.OptOut);
    MigrationScript.Prepare(sql).Should().Be(sql);
  }

  [Fact]
  public void MarkerInsideSql_ButNotOnItsOwnCommentLine_DoesNotOptOut()
  {
    MigrationScript.Classify("SELECT '-- buoy:no-transaction';").Should().Be(MigrationTransactionMode.Wrapped);
  }

  [Theory]
  [InlineData("-- header\nBEGIN;\nCREATE TABLE t(a int);\nCOMMIT;")]
  [InlineData("begin;\nSELECT 1;\ncommit;")]
  [InlineData("START TRANSACTION;\nSELECT 1;\nCOMMIT;")]
  public void LegacyScriptWithOwnBegin_IsLeftAlone(string sql)
  {
    MigrationScript.Classify(sql).Should().Be(MigrationTransactionMode.SelfManaged);
    MigrationScript.Prepare(sql).Should().Be(sql);
  }

  [Fact]
  public void PlpgsqlBegin_InsideDoBlock_IsNotATransactionStart()
  {
    var sql = "DO $$\nBEGIN\n  PERFORM 1;\nEND $$;";
    MigrationScript.Classify(sql).Should().Be(MigrationTransactionMode.Wrapped);
  }

  [Fact]
  public void BeginInsideComment_IsIgnored()
  {
    MigrationScript.Classify("-- BEGIN;\nSELECT 1;").Should().Be(MigrationTransactionMode.Wrapped);
  }

  [Fact]
  public void WrapWithJournal_PutsTheJournalRowInsideTheTransaction()
  {
    var sql = MigrationScript.WrapWithJournal("SELECT 1", "ns.0048_it's.up.sql", "\"public\".\"schemaversions\"");

    sql.Should().StartWith("BEGIN;").And.EndWith("COMMIT;\n");
    sql.Should().Contain("'ns.0048_it''s.up.sql'");
    sql.IndexOf("INSERT INTO", StringComparison.Ordinal).Should().BeGreaterThan(sql.IndexOf("SELECT 1", StringComparison.Ordinal))
      .And.BeLessThan(sql.LastIndexOf("COMMIT;", StringComparison.Ordinal));
  }
}
