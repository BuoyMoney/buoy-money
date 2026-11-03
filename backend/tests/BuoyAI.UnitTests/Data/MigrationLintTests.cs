using BuoyAI.Infrastructure.Data;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Data;

public class MigrationLintTests
{
  private static MigrationFile Up(string name, string sql = "ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"X\" int;") =>
    new(name, sql);

  private static IReadOnlyList<string> Lint(MigrationFile[] ups, params string[] extraDowns)
  {
    var downs = ups.Select(u => u.Name.Replace(".up.sql", ".down.sql")).Concat(extraDowns).ToArray();
    return MigrationLint.Check(ups, downs);
  }

  // --- fixtures ---

  [Fact]
  public void CleanScript_Passes()
  {
    Lint(new[] { Up("0048_add_thing.up.sql") }).Should().BeEmpty();
  }

  [Fact]
  public void DuplicateNumber_ForNewScripts_IsRejected()
  {
    var problems = Lint(new[] { Up("0048_add_a.up.sql"), Up("0048_add_b.up.sql") });

    problems.Should().ContainSingle().Which.Should().Contain("duplicate migration number 0048");
  }

  [Fact]
  public void GrandfatheredDuplicates_ArePermitted_ButOnlyTheExactPairs()
  {
    Lint(new[]
    {
      Up("0013_add_unique_active_subscription_per_user.up.sql"),
      Up("0013_budget_category_integrity.up.sql"),
    }).Should().BeEmpty();

    // A third script sharing a grandfathered number is a new duplicate.
    Lint(new[]
    {
      Up("0013_add_unique_active_subscription_per_user.up.sql"),
      Up("0013_budget_category_integrity.up.sql"),
      Up("0013_something_new.up.sql"),
    }).Should().Contain(p => p.Contains("duplicate migration number 0013"));
  }

  [Fact]
  public void MissingDownScript_IsRejected()
  {
    var problems = MigrationLint.Check(new[] { Up("0048_add_thing.up.sql") }, Array.Empty<string>());

    problems.Should().ContainSingle().Which.Should().Contain("missing down script 0048_add_thing.down.sql");
  }

  [Theory]
  [InlineData("DROP TABLE \"Old\";", "DROP TABLE")]
  [InlineData("ALTER TABLE \"Users\" DROP COLUMN \"X\";", "DROP COLUMN")]
  [InlineData("TRUNCATE \"Logs\";", "TRUNCATE")]
  [InlineData("DELETE FROM \"Logs\";", "DELETE without WHERE")]
  [InlineData("DELETE FROM \"Logs\" USING (SELECT 1 WHERE true) s;", "DELETE without WHERE")]
  [InlineData("WITH x AS (SELECT 1 WHERE true) DELETE FROM \"Logs\";", "DELETE without WHERE")]
  [InlineData("ALTER TABLE \"Users\" DROP \"X\";", "DROP COLUMN")]
  [InlineData("ALTER TABLE \"Users\" DROP IF EXISTS x;", "DROP COLUMN")]
  [InlineData("ALTER TABLE \"T\" ALTER \"C\" TYPE bigint;", "ALTER COLUMN ... TYPE")]
  [InlineData("ALTER TABLE \"T\" ALTER COLUMN \"C\" TYPE bigint;", "ALTER COLUMN ... TYPE")]
  [InlineData("ALTER TABLE \"T\"\n  ALTER COLUMN \"C\"\n  SET DATA TYPE text;", "ALTER COLUMN ... TYPE")]
  [InlineData("DO $$ BEGIN\n  ALTER TABLE \"T\" ALTER COLUMN \"C\" TYPE bigint;\nEND $$;", "ALTER COLUMN ... TYPE")]
  public void DestructiveStatement_IsRejected(string sql, string expected)
  {
    var problems = Lint(new[] { Up("0048_x.up.sql", sql) });

    problems.Should().ContainSingle().Which.Should().Contain("destructive statement").And.Contain(expected);
  }

  [Fact]
  public void DestructiveStatement_WithReviewedMarker_Passes()
  {
    var sql = "-- buoy:reviewed-destructive: column unused since v1.5.20, no running version reads it\nALTER TABLE \"Users\" DROP COLUMN \"Old\";";

    Lint(new[] { Up("0048_x.up.sql", sql) }).Should().BeEmpty();
  }

  [Fact]
  public void ReviewedMarker_WithoutAReason_DoesNotCount()
  {
    var sql = "-- buoy:reviewed-destructive:\nDROP TABLE \"Old\";";

    Lint(new[] { Up("0048_x.up.sql", sql) }).Should().ContainSingle().Which.Should().Contain("destructive");
  }

  [Theory]
  [InlineData("DELETE FROM \"Logs\" WHERE \"Id\" < 5;")]
  [InlineData("WITH x AS (SELECT 1) DELETE FROM \"Logs\" WHERE \"Id\" IN (SELECT 1);")]
  [InlineData("-- DROP TABLE mentioned in a comment\nSELECT 1;")]
  [InlineData("DROP INDEX IF EXISTS ix_old;")]
  [InlineData("ALTER TABLE \"T\" DROP CONSTRAINT IF EXISTS fk_x;")]
  [InlineData("ALTER TABLE \"T\" ALTER \"C\" DROP DEFAULT;")]
  [InlineData("ALTER TABLE \"T\" ALTER COLUMN \"C\" DROP NOT NULL;")]
  public void SafeStatements_Pass(string sql)
  {
    Lint(new[] { Up("0048_x.up.sql", sql) }).Should().BeEmpty();
  }

  [Fact]
  public void ConcurrentIndex_WithoutMarker_IsRejected_WithMarker_Passes()
  {
    const string sql = "CREATE INDEX CONCURRENTLY IF NOT EXISTS ix ON \"T\"(\"C\");";

    Lint(new[] { Up("0048_x.up.sql", sql) }).Should().ContainSingle().Which.Should().Contain("cannot run in a transaction");
    Lint(new[] { Up("0048_x.up.sql", "-- buoy:no-transaction\n" + sql) }).Should().BeEmpty();
  }

  [Fact]
  public void NewScript_WithOwnBeginOrCommit_IsRejected()
  {
    Lint(new[] { Up("0048_x.up.sql", "BEGIN;\nSELECT 1;\nCOMMIT;") }).Should().Contain(p => p.Contains("must not open their own BEGIN"));
    Lint(new[] { Up("0048_x.up.sql", "SELECT 1;\nCOMMIT;") }).Should().Contain(p => p.Contains("remove COMMIT"));
  }

  [Fact]
  public void NewScript_WithNoTransactionMarker_AndOwnBeginCommit_IsRejected()
  {
    var sql = "-- buoy:no-transaction\nBEGIN;\nSELECT 1;\nCOMMIT;";

    Lint(new[] { Up("0048_x.up.sql", sql) }).Should().ContainSingle().Which.Should().Contain("must not manage their own transaction");
  }

  [Fact]
  public void LegacyScript_WithOwnBegin_IsNotFlagged()
  {
    Lint(new[] { Up("0046_x.up.sql", "BEGIN;\nSELECT 1;\nCOMMIT;") }).Should().BeEmpty();
  }

  [Fact]
  public void BadName_IsRejected()
  {
    Lint(new[] { Up("48_thing.up.sql") }).Should().ContainSingle().Which.Should().Contain("name must look like");
  }

  // --- the real folder ---

  private static string SqlFolder()
  {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
      var candidate = Path.Combine(dir.FullName, "src", "BuoyAI.Infrastructure", "Migrations", "Sql");
      if (Directory.Exists(candidate)) return candidate;
      dir = dir.Parent;
    }
    throw new DirectoryNotFoundException("Could not locate backend/src/BuoyAI.Infrastructure/Migrations/Sql.");
  }

  [Fact]
  public void RealMigrations_PassTheLint()
  {
    var folder = SqlFolder();
    var ups = Directory.GetFiles(folder, "*.up.sql")
      .Select(f => new MigrationFile(Path.GetFileName(f), File.ReadAllText(f)))
      .ToArray();
    var downs = Directory.GetFiles(folder, "*.down.sql").Select(f => Path.GetFileName(f)!).ToArray();

    ups.Should().NotBeEmpty();
    MigrationLint.Check(ups, downs).Should().BeEmpty("every migration must satisfy the lint (see MigrationLint)");
  }

  [Fact]
  public void GrandfatheredDuplicates_AllExistOnDisk_SoTheAllowlistCannotRot()
  {
    var names = Directory.GetFiles(SqlFolder(), "*.up.sql").Select(f => Path.GetFileName(f)!).ToHashSet();

    foreach (var file in MigrationLint.GrandfatheredDuplicates.Values.SelectMany(v => v))
    {
      names.Should().Contain(file);
    }
    foreach (var file in MigrationLint.GrandfatheredDestructive)
    {
      names.Should().Contain(file);
    }
  }

  [Fact]
  public void EveryScriptAboveTheCutoff_IsWrappedOrOptedOut()
  {
    foreach (var f in Directory.GetFiles(SqlFolder(), "*.up.sql"))
    {
      var number = int.Parse(Path.GetFileName(f)[..4]);
      if (number <= MigrationLint.LegacyCutoff) continue;

      MigrationScript.Classify(File.ReadAllText(f)).Should().NotBe(MigrationTransactionMode.SelfManaged, f);
    }
  }
}
