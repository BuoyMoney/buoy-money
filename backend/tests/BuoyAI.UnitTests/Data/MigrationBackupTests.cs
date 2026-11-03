using BuoyAI.Infrastructure.Data;
using DbUp.Engine;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Data;

public class MigrationBackupTests : IDisposable
{
  private const string Conn = "Host=db.local;Port=5433;Username=buoy;Password=s3cret;Database=BuoyAI";
  private readonly string _dir = Path.Combine(Path.GetTempPath(), "bm06-backup-" + Guid.NewGuid().ToString("N"));

  public void Dispose()
  {
    if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
  }

  private MigrationSettings Settings(Action<MigrationSettings>? tweak = null)
  {
    var s = new MigrationSettings { BackupBeforeMigrate = true, BackupDirectory = _dir };
    tweak?.Invoke(s);
    return s;
  }

  /// <summary>Fake pg_dump / pg_restore: records calls and writes the dump file.</summary>
  private sealed class FakeRunner : IProcessRunner
  {
    public List<ProcessRunRequest> Calls { get; } = new();
    public int DumpExit { get; set; }
    public string FileContent { get; set; } = "PGDMP";
    public string DumpErr { get; set; } = "connection refused";
    public int ListExit { get; set; }
    public string ListOut { get; set; } = "; Archive created";

    public ProcessRunResult Run(ProcessRunRequest request)
    {
      Calls.Add(request);
      if (request.Arguments.Contains("--list"))
      {
        return new ProcessRunResult(ListExit, ListOut, ListExit == 0 ? "" : "bad archive");
      }
      if (DumpExit == 0)
      {
        var file = request.Arguments[request.Arguments.ToList().IndexOf("--file") + 1];
        File.WriteAllText(file, FileContent);
      }
      return new ProcessRunResult(DumpExit, "", DumpExit == 0 ? "" : DumpErr);
    }
  }

  [Fact]
  public void Success_DumpsThenVerifiesWithPgRestoreList()
  {
    var runner = new FakeRunner();

    var file = MigrationBackup.Create(Conn, Settings(), runner);

    File.Exists(file).Should().BeTrue();
    runner.Calls.Should().HaveCount(2);
    runner.Calls[0].FileName.Should().Be("pg_dump");
    runner.Calls[1].FileName.Should().Be("pg_restore");
    runner.Calls[1].Arguments.Should().Contain("--list").And.Contain(file);
  }

  [Fact]
  public void Password_GoesInEnvironment_NotOnTheCommandLine()
  {
    var runner = new FakeRunner();

    MigrationBackup.Create(Conn, Settings(), runner);

    runner.Calls[0].Environment["PGPASSWORD"].Should().Be("s3cret");
    runner.Calls[0].Arguments.Should().NotContain(a => a.Contains("s3cret"));
    runner.Calls[0].Arguments.Should().ContainInOrder("--host", "db.local", "--port", "5433", "--username", "buoy");
  }

  [Fact]
  public void DumpFailure_Throws_AndLeavesNoPartialFile()
  {
    var runner = new FakeRunner { DumpExit = 1 };

    var act = () => MigrationBackup.Create(Conn, Settings(), runner);

    act.Should().Throw<MigrationBackupException>().WithMessage("*pg_dump exited with 1*");
    Directory.GetFiles(_dir).Should().BeEmpty();
  }

  [Fact]
  public void EmptyDump_Throws()
  {
    var runner = new FakeRunner { FileContent = "" };

    var act = () => MigrationBackup.Create(Conn, Settings(), runner);

    act.Should().Throw<MigrationBackupException>().WithMessage("*missing or empty*");
    Directory.GetFiles(_dir).Should().BeEmpty();
  }

  [Fact]
  public void UnreadableArchive_Throws_AndDeletesTheDump()
  {
    var runner = new FakeRunner { ListExit = 1 };

    var act = () => MigrationBackup.Create(Conn, Settings(), runner);

    act.Should().Throw<MigrationBackupException>().WithMessage("*could not be verified*");
    Directory.GetFiles(_dir).Should().BeEmpty();
  }

  [Fact]
  public void Prune_KeepsOnlyTheNewestN()
  {
    var runner = new FakeRunner();
    var settings = Settings(s => s.KeepBackups = 2);
    var clock = new DateTime(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc);

    for (var i = 0; i < 4; i++)
    {
      var now = clock.AddMinutes(i);
      MigrationBackup.Create(Conn, settings, runner, null, () => now);
    }

    var names = Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
    names.Should().HaveCount(2);
    names[1].Should().Contain("20261012T010300Z");
  }

  [Fact]
  public void SameSecondRetries_GetDistinctFiles_AndAFailedRetryKeepsTheEarlierDump()
  {
    var now = new DateTime(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc);

    var first = MigrationBackup.Create(Conn, Settings(), new FakeRunner(), null, () => now);
    var failing = () => MigrationBackup.Create(Conn, Settings(), new FakeRunner { ListExit = 1 }, null, () => now);

    failing.Should().Throw<MigrationBackupException>();
    File.Exists(first).Should().BeTrue("a failed retry must not delete the earlier verified dump");
  }

  [Fact]
  public void SameSecondBackups_WithKeepOne_AlwaysKeepTheFileJustWritten()
  {
    var now = new DateTime(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc);
    var settings = Settings(s => s.KeepBackups = 1);

    for (var i = 0; i < 30; i++)
    {
      var file = MigrationBackup.Create(Conn, settings, new FakeRunner(), null, () => now);
      File.Exists(file).Should().BeTrue("the archive Create returns must never be pruned");
    }

    Directory.GetFiles(_dir).Should().HaveCount(1);
  }

  [Fact]
  public void LongStderr_IsCutWithoutSplittingASurrogatePair()
  {
    // The emoji's high surrogate sits at index 499, so a raw [..500] slice would keep half of it.
    var runner = new FakeRunner { DumpExit = 1, DumpErr = new string('x', 499) + "😀tail" };

    var act = () => MigrationBackup.Create(Conn, Settings(), runner);

    var message = act.Should().Throw<MigrationBackupException>().Which.Message;
    message.Any(char.IsHighSurrogate).Should().BeFalse();
    message.Any(char.IsLowSurrogate).Should().BeFalse();
  }

  [Fact]
  public void OnUnix_TheDumpAndNewDirectoryAreOwnerOnly()
  {
    if (OperatingSystem.IsWindows()) return;

    var file = MigrationBackup.Create(Conn, Settings(), new FakeRunner());

    File.GetUnixFileMode(file).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    (File.GetUnixFileMode(_dir) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
      | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)).Should().Be(0);
  }

  [Fact]
  public void UnwritableBackupDirectory_IsReportedAsABackupFailure()
  {
    Directory.CreateDirectory(_dir);
    var blocker = Path.Combine(_dir, "not-a-directory");
    File.WriteAllText(blocker, "x");

    var act = () => MigrationBackup.Create(Conn, Settings(s => s.BackupDirectory = blocker), new FakeRunner());

    act.Should().Throw<MigrationBackupException>();
  }

  // The gate in the runner: the backup runs only when enabled, on an existing database with
  // pending scripts, and a failure refuses to migrate unless explicitly overridden.

  private static SqlScript A => new("0001_a.up.sql", "CREATE TABLE \"A\" (id int);");

  private static SqlScript B => new("0002_b.up.sql", "CREATE TABLE \"B\" (id int);");

  private static void Run(ScratchDatabase db, MigrationSettings? settings, FakeRunner? runner, params SqlScript[] scripts) =>
    SqlMigrationRunner.Run(db.ConnectionString, null, settings, runner, null, scripts);

  [PostgresFact]
  public void Gate_FailedBackup_RefusesToMigrate_AndChangesNothing()
  {
    using var db = new ScratchDatabase();
    Run(db, null, null, A);

    var act = () => Run(db, Settings(), new FakeRunner { DumpExit = 1 }, A, B);

    act.Should().Throw<InvalidOperationException>().WithMessage("*Refusing to apply database migrations*");
    db.TableExists("B").Should().BeFalse();
  }

  [PostgresFact]
  public void Gate_Override_MigratesEvenWhenBackupFails()
  {
    using var db = new ScratchDatabase();
    Run(db, null, null, A);

    Run(db, Settings(s => s.AllowMigrateWithoutBackup = true), new FakeRunner { DumpExit = 1 }, A, B);

    db.TableExists("B").Should().BeTrue();
  }

  [PostgresFact]
  public void Gate_DisabledByDefault_NeverCallsPgDump()
  {
    using var db = new ScratchDatabase();
    var runner = new FakeRunner();
    Run(db, null, null, A);

    Run(db, new MigrationSettings(), runner, A, B);

    runner.Calls.Should().BeEmpty();
    db.TableExists("B").Should().BeTrue();
  }

  [PostgresFact]
  public void Gate_FreshDatabase_OrNothingPending_SkipsTheBackup()
  {
    using var db = new ScratchDatabase();
    var runner = new FakeRunner();

    Run(db, Settings(), runner, A); // fresh: no state to protect
    Run(db, Settings(), runner, A); // up to date: nothing pending

    runner.Calls.Should().BeEmpty();
  }

  [PostgresFact]
  public void Gate_ExistingDatabaseWithPending_TakesBackupBeforeMigrating()
  {
    using var db = new ScratchDatabase();
    Run(db, null, null, A);
    var runner = new FakeRunner();

    Run(db, Settings(), runner, A, B);

    runner.Calls.Select(c => c.FileName).Should().Equal("pg_dump", "pg_restore");
    db.TableExists("B").Should().BeTrue();
  }
}
