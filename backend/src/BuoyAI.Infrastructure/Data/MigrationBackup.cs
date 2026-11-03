using System.Diagnostics;
using System.Globalization;
using BuoyAI.Core.Utilities;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BuoyAI.Infrastructure.Data;

/// <summary>
/// Settings for the migration runner, bound from the <c>Migrations</c> config section.
/// Everything defaults to the hosted behaviour (no backup step): hosted prod relies on its
/// blue-green deploy. Self-host turns <see cref="BackupBeforeMigrate"/> on.
/// </summary>
public sealed class MigrationSettings
{
  /// <summary>Take and verify a pg_dump before applying pending migrations to an existing database.</summary>
  public bool BackupBeforeMigrate { get; set; }

  /// <summary>Where dumps are written. Relative paths resolve against the working directory.</summary>
  public string BackupDirectory { get; set; } = "migration-backups";

  /// <summary>Escape hatch: migrate even when the backup fails. Off by default; the failure is logged loudly.</summary>
  public bool AllowMigrateWithoutBackup { get; set; }

  public string PgDumpPath { get; set; } = "pg_dump";

  public string PgRestorePath { get; set; } = "pg_restore";

  /// <summary>Newest dumps to keep in <see cref="BackupDirectory"/>. 0 keeps everything.</summary>
  public int KeepBackups { get; set; } = 5;
}

public sealed record ProcessRunRequest(
  string FileName,
  IReadOnlyList<string> Arguments,
  IReadOnlyDictionary<string, string> Environment);

public sealed record ProcessRunResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Seam over starting an external process, so the backup gate can be tested without pg_dump.</summary>
public interface IProcessRunner
{
  ProcessRunResult Run(ProcessRunRequest request);
}

internal sealed class SystemProcessRunner : IProcessRunner
{
  private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(60);

  public ProcessRunResult Run(ProcessRunRequest request)
  {
    var psi = new ProcessStartInfo(request.FileName)
    {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    foreach (var a in request.Arguments)
    {
      psi.ArgumentList.Add(a);
    }
    foreach (var (k, v) in request.Environment)
    {
      psi.Environment[k] = v;
    }

    try
    {
      using var p = Process.Start(psi) ?? throw new MigrationBackupException($"Could not start '{request.FileName}'.");
      var stdout = p.StandardOutput.ReadToEndAsync();
      var stderr = p.StandardError.ReadToEndAsync();
      if (!p.WaitForExit(Timeout))
      {
        try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        throw new MigrationBackupException($"'{request.FileName}' did not finish within {Timeout.TotalMinutes:0} minutes.");
      }
      return new ProcessRunResult(p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
    catch (System.ComponentModel.Win32Exception ex)
    {
      throw new MigrationBackupException(
        $"Could not run '{request.FileName}' ({ex.Message}). Install the PostgreSQL client tools or set Migrations:PgDumpPath / PgRestorePath.", ex);
    }
  }
}

public sealed class MigrationBackupException : Exception
{
  public MigrationBackupException(string message) : base(message) { }

  public MigrationBackupException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Takes a custom-format pg_dump before migrating and checks it is usable (non-empty and
/// <c>pg_restore --list</c> can read it). A dump that cannot be verified is deleted and reported
/// as a failure so the caller can refuse to migrate.
/// </summary>
public static class MigrationBackup
{
  public static string Create(
    string connectionString,
    MigrationSettings settings,
    IProcessRunner runner,
    ILogger? logger = null,
    Func<DateTime>? utcNow = null)
  {
    try
    {
      return CreateCore(connectionString, settings, runner, logger, utcNow);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
    {
      // Directory/path/connection-string problems are backup failures too, so the caller's
      // AllowMigrateWithoutBackup override covers them.
      throw new MigrationBackupException($"The backup could not be written: {ex.Message}", ex);
    }
  }

  private static string CreateCore(
    string connectionString,
    MigrationSettings settings,
    IProcessRunner runner,
    ILogger? logger,
    Func<DateTime>? utcNow)
  {
    var csb = new NpgsqlConnectionStringBuilder(connectionString);
    var database = csb.Database ?? throw new MigrationBackupException("Connection string has no database name.");

    CreatePrivateDirectory(settings.BackupDirectory);
    var stamp = (utcNow ?? (() => DateTime.UtcNow))().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    // The random suffix gives every attempt its own file: a retry within the same second must not
    // reuse (and, on failure, delete) an earlier verified dump. The stamp still sorts by age.
    var unique = Guid.NewGuid().ToString("N")[..8];
    var file = Path.GetFullPath(Path.Combine(settings.BackupDirectory, $"{SafeName(database)}_pre-migrate_{stamp}_{unique}.dump"));

    // The password goes in PGPASSWORD, never on the command line (visible in process lists).
    var env = new Dictionary<string, string>();
    if (!string.IsNullOrEmpty(csb.Password))
    {
      env["PGPASSWORD"] = csb.Password;
    }
    var sslMode = csb.SslMode switch
    {
      SslMode.Disable => "disable",
      SslMode.Require => "require",
      SslMode.VerifyCA => "verify-ca",
      SslMode.VerifyFull => "verify-full",
      _ => null,
    };
    if (sslMode is not null)
    {
      env["PGSSLMODE"] = sslMode;
    }

    var host = (csb.Host ?? "localhost").Split(',')[0].Trim();
    var dumpArgs = new List<string>
    {
      "--format=custom", "--no-password",
      "--host", host, "--port", csb.Port.ToString(CultureInfo.InvariantCulture),
      "--username", csb.Username ?? "postgres",
      "--file", file,
      database,
    };

    try
    {
      // pg_dump keeps the mode of a file that already exists, so pre-create it owner-only (0600 on
      // Unix; on Windows it inherits the backup directory's ACL).
      var precreate = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
      if (!OperatingSystem.IsWindows())
      {
        precreate.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
      }
      using (new FileStream(file, precreate)) { }

      var dump = runner.Run(new ProcessRunRequest(settings.PgDumpPath, dumpArgs, env));
      if (dump.ExitCode != 0)
      {
        throw new MigrationBackupException(
          $"pg_dump exited with {dump.ExitCode}: {Truncate(dump.StdErr)}. " +
          "A pg_dump older than the server cannot dump it; use a matching PostgreSQL client.");
      }

      if (!File.Exists(file) || new FileInfo(file).Length == 0)
      {
        throw new MigrationBackupException($"pg_dump reported success but '{file}' is missing or empty.");
      }

      var list = runner.Run(new ProcessRunRequest(settings.PgRestorePath, new[] { "--list", file }, env));
      if (list.ExitCode != 0 || string.IsNullOrWhiteSpace(list.StdOut))
      {
        throw new MigrationBackupException(
          $"The backup could not be verified: pg_restore --list exited with {list.ExitCode}: {Truncate(list.StdErr)}.");
      }
    }
    catch
    {
      TryDelete(file);
      throw;
    }

    logger?.LogInformation("Pre-migration backup written and verified: {File} ({Bytes} bytes)", file, new FileInfo(file).Length);
    Prune(settings, database, file, logger);
    return file;
  }

  private static void Prune(MigrationSettings settings, string database, string current, ILogger? logger)
  {
    if (settings.KeepBackups <= 0)
    {
      return;
    }
    try
    {
      // Names embed a sortable UTC stamp, but the random suffix breaks ties arbitrarily within a
      // second. The archive just written is always kept; the newest N-1 others fill the rest.
      var old = Directory.GetFiles(settings.BackupDirectory, $"{SafeName(database)}_pre-migrate_*.dump")
        .Select(Path.GetFullPath)
        .Where(f => !string.Equals(f, current, StringComparison.Ordinal))
        .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
        .Skip(settings.KeepBackups - 1);
      foreach (var f in old)
      {
        TryDelete(f);
      }
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
      logger?.LogWarning(ex, "Could not prune old migration backups in {Dir}", settings.BackupDirectory);
    }
  }

  private static string SafeName(string s) =>
    new(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

  private static string Truncate(string s) => s.Length <= 500 ? s.Trim() : SafeText.Truncate(s, 500).Trim() + "...";

  private static void CreatePrivateDirectory(string dir)
  {
    if (OperatingSystem.IsWindows())
    {
      Directory.CreateDirectory(dir);
      return;
    }
    // A newly created directory is owner-only (0700). An existing one is left as the operator set it.
    Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
  }

  private static void TryDelete(string path)
  {
    try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
  }
}
