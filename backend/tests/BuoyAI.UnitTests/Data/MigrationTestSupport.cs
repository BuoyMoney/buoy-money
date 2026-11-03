using Npgsql;
using Xunit;

namespace BuoyAI.UnitTests.Data;

/// <summary>Fact that only runs when a throwaway Postgres server is available (CI service container
/// or scripts/dev/migration-upgrade-check.sh). Set BUOYAI_MIGRATION_TEST_CONNECTION to a superuser
/// connection string; each test creates and drops its own database.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
  public const string EnvVar = "BUOYAI_MIGRATION_TEST_CONNECTION";

  public PostgresFactAttribute(string envVar = EnvVar)
  {
    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envVar)))
    {
      Skip = $"Set {envVar} to run migration tests against a real Postgres.";
    }
  }
}

/// <summary>A scratch database, dropped on dispose.</summary>
public sealed class ScratchDatabase : IDisposable
{
  private readonly string _adminConnection;

  public string Name { get; } = "bm_" + Guid.NewGuid().ToString("N")[..12];

  public string ConnectionString { get; }

  public ScratchDatabase(string envVar = PostgresFactAttribute.EnvVar)
  {
    _adminConnection = Environment.GetEnvironmentVariable(envVar)!;
    Exec(_adminConnection, $"CREATE DATABASE \"{Name}\"");
    ConnectionString = new NpgsqlConnectionStringBuilder(_adminConnection) { Database = Name, Pooling = false }.ConnectionString;
  }

  public object? Scalar(string sql)
  {
    using var c = new NpgsqlConnection(ConnectionString);
    c.Open();
    using var cmd = new NpgsqlCommand(sql, c);
    return cmd.ExecuteScalar();
  }

  public void Execute(string sql) => Exec(ConnectionString, sql);

  public bool TableExists(string name) => (bool)Scalar($"SELECT to_regclass('public.\"{name}\"') IS NOT NULL")!;

  public long JournalCount() => TableExists("schemaversions") ? (long)Scalar("SELECT count(*) FROM schemaversions")! : 0;

  public void Dispose()
  {
    try
    {
      NpgsqlConnection.ClearAllPools();
      Exec(_adminConnection, $"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)");
    }
    catch (NpgsqlException)
    {
      // best effort: a leftover scratch database is harmless
    }
  }

  private static void Exec(string cs, string sql)
  {
    using var c = new NpgsqlConnection(cs);
    c.Open();
    using var cmd = new NpgsqlCommand(sql, c);
    cmd.ExecuteNonQuery();
  }
}
