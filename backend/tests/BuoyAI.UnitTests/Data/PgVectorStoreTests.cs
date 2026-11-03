using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace BuoyAI.UnitTests.Data;

/// <summary>
/// PgVectorStore against real servers. The pgvector tests need BUOYAI_PGVECTOR_TEST_CONNECTION (a
/// superuser connection to a pgvector/pgvector:pg16 server); the without-extension tests use the
/// plain Postgres from BUOYAI_MIGRATION_TEST_CONNECTION. Both skip when unset.
/// </summary>
public class PgVectorStoreTests
{
  private const string PgVectorEnv = "BUOYAI_PGVECTOR_TEST_CONNECTION";

  private static PgVectorStore StoreFor(ScratchDatabase db) => new(db.ConnectionString, NullLogger<PgVectorStore>.Instance);

  private static VectorPoint Point(string key, float[] v, int userId, string model = "m1", string? content = null) =>
    new(key, v, model, new Dictionary<string, object> { ["user_id"] = userId, ["conversation_id"] = 7, ["content"] = content ?? key });

  [Fact]
  public void Vector_literal_is_invariant_culture_and_round_trips_floats()
  {
    PgVectorStore.ToVectorLiteral([0.5f, -1.25f, 1e-7f, 3f]).Should().Be("[0.5,-1.25,1E-07,3]");
  }

  [Fact]
  public void Payload_values_come_back_as_strings()
  {
    var payload = PgVectorStore.ReadPayload("""{"a":"x","n":42,"s":"with \"quotes\""}""");

    payload["a"].Should().Be("x");
    payload["n"].Should().Be("42");
    payload["s"].Should().Be("with \"quotes\"");
  }

  [Fact]
  public async Task Without_a_connection_string_the_store_is_off_and_every_call_is_a_no_op()
  {
    var store = new PgVectorStore(null, NullLogger<PgVectorStore>.Instance);

    store.IsEnabled.Should().BeFalse();
    await store.UpsertAsync("c", Point("1", [1f], 1));
    (await store.SearchAsync("c", [1f], "m1", 5)).Should().BeEmpty();
    await store.DeleteByFieldAsync("c", "user_id", 1);
  }

  [PostgresFact(PgVectorEnv)]
  public void Migration_creates_the_embeddings_table_when_the_extension_is_available()
  {
    using var db = new ScratchDatabase(PgVectorEnv);

    SqlMigrationRunner.Run(db.ConnectionString);

    db.TableExists("embeddings").Should().BeTrue();
    db.Scalar("SELECT count(*) FROM pg_extension WHERE extname = 'vector'").Should().Be(1L);
    var again = () => SqlMigrationRunner.Run(db.ConnectionString);
    again.Should().NotThrow();
  }

  [PostgresFact]
  public async Task Migration_without_the_extension_is_a_no_op_and_the_store_turns_itself_off()
  {
    using var db = new ScratchDatabase();

    var run = () => SqlMigrationRunner.Run(db.ConnectionString);
    run.Should().NotThrow("hosted prod has no pgvector yet and must still migrate");
    db.TableExists("embeddings").Should().BeFalse();

    var store = StoreFor(db);
    store.IsEnabled.Should().BeTrue("nothing has been checked yet");
    await store.EnsureCollectionAsync("c", 3);
    store.IsEnabled.Should().BeFalse();
    await store.UpsertAsync("c", Point("1", [1f, 0f, 0f], 1));
    (await store.SearchAsync("c", [1f, 0f, 0f], "m1", 5)).Should().BeEmpty();
  }

  [PostgresFact(PgVectorEnv)]
  public async Task Store_creates_the_table_when_the_migration_ran_before_the_extension_existed()
  {
    using var db = new ScratchDatabase(PgVectorEnv);
    SqlMigrationRunner.Run(db.ConnectionString);
    db.Execute("DROP TABLE public.embeddings");

    var store = StoreFor(db);
    await store.EnsureCollectionAsync("c", 3);

    store.IsEnabled.Should().BeTrue();
    db.TableExists("embeddings").Should().BeTrue();
  }

  [PostgresFact(PgVectorEnv)]
  public async Task Upsert_replaces_by_key_and_search_ranks_by_cosine_similarity()
  {
    using var db = new ScratchDatabase(PgVectorEnv);
    SqlMigrationRunner.Run(db.ConnectionString);
    var store = StoreFor(db);

    await store.UpsertAsync("c", Point("a", [1f, 0f, 0f], 1));
    await store.UpsertAsync("c", Point("b", [1f, 1f, 0f], 1));
    await store.UpsertAsync("c", Point("c", [0f, 0f, 1f], 1));
    await store.UpsertAsync("c", Point("a", [1f, 0f, 0f], 1, content: "replaced"));

    db.Scalar("SELECT count(*) FROM embeddings").Should().Be(3L);

    var hits = await store.SearchAsync("c", [1f, 0.1f, 0f], "m1", limit: 10);
    hits.Select(h => h.Key).Should().Equal("a", "b", "c");
    hits[0].Score.Should().BeApproximately(0.995f, 0.001f);
    hits[0].Get("content").Should().Be("replaced");
    hits[0].GetInt("conversation_id").Should().Be(7);
    hits[2].Score.Should().BeApproximately(0f, 0.001f);

    (await store.SearchAsync("c", [1f, 0.1f, 0f], "m1", limit: 1)).Should().ContainSingle().Which.Key.Should().Be("a");
    (await store.SearchAsync("c", [1f, 0.1f, 0f], "m1", limit: 10, minScore: 0.7f)).Select(h => h.Key).Should().Equal("a", "b");
  }

  [PostgresFact(PgVectorEnv)]
  public async Task Search_is_scoped_to_collection_user_model_and_dimension()
  {
    using var db = new ScratchDatabase(PgVectorEnv);
    SqlMigrationRunner.Run(db.ConnectionString);
    var store = StoreFor(db);

    await store.UpsertAsync("c", Point("mine", [1f, 0f, 0f], userId: 1));
    await store.UpsertAsync("c", Point("theirs", [1f, 0f, 0f], userId: 2));
    await store.UpsertAsync("other", Point("elsewhere", [1f, 0f, 0f], userId: 1));
    await store.UpsertAsync("c", Point("old-model", [1f, 0f, 0f], userId: 1, model: "m0"));
    await store.UpsertAsync("c", Point("wide", [1f, 0f, 0f, 0f, 0f], userId: 1, model: "m2"));

    (await store.SearchAsync("c", [1f, 0f, 0f], "m1", 10, userId: 1)).Select(h => h.Key).Should().Equal("mine");
    (await store.SearchAsync("c", [1f, 0f, 0f], "m1", 10)).Select(h => h.Key).Should().BeEquivalentTo("mine", "theirs");

    // A 5-dim query must not trip over the 3-dim rows (pgvector rejects mixed dimensions).
    (await store.SearchAsync("c", [1f, 0f, 0f, 0f, 0f], "m2", 10)).Select(h => h.Key).Should().Equal("wide");

    db.Scalar("SELECT dimensions FROM embeddings WHERE key = 'wide'").Should().Be(5);
    db.Scalar("SELECT model FROM embeddings WHERE key = 'old-model'").Should().Be("m0");
  }

  [PostgresFact(PgVectorEnv)]
  public async Task Delete_by_field_removes_a_users_rows_or_a_payload_match_only_in_that_collection()
  {
    using var db = new ScratchDatabase(PgVectorEnv);
    SqlMigrationRunner.Run(db.ConnectionString);
    var store = StoreFor(db);

    await store.UpsertAsync("c", Point("1", [1f, 0f, 0f], userId: 1));
    await store.UpsertAsync("c", Point("2", [1f, 0f, 0f], userId: 2));
    await store.UpsertAsync("other", Point("3", [1f, 0f, 0f], userId: 1));

    await store.DeleteByFieldAsync("c", "conversation_id", 7);
    db.Scalar("SELECT count(*) FROM embeddings WHERE collection = 'c'").Should().Be(0L);
    db.Scalar("SELECT count(*) FROM embeddings WHERE collection = 'other'").Should().Be(1L);

    await store.UpsertAsync("c", Point("1", [1f, 0f, 0f], userId: 1));
    await store.UpsertAsync("c", Point("2", [1f, 0f, 0f], userId: 2));
    await store.DeleteByFieldAsync("c", "user_id", 1);
    db.Scalar("SELECT key FROM embeddings WHERE collection = 'c'").Should().Be("2");
  }

  [PostgresFact(PgVectorEnv)]
  public async Task Wrong_dimension_for_a_row_is_impossible_by_construction()
  {
    using var db = new ScratchDatabase(PgVectorEnv);
    SqlMigrationRunner.Run(db.ConnectionString);

    var act = () => db.Execute(
      "INSERT INTO embeddings (collection, key, model, dimensions, embedding) VALUES ('c', 'k', 'm', 4, '[1,2,3]')");

    act.Should().Throw<PostgresException>().Which.ConstraintName.Should().Be("ck_embeddings_dimensions");
    await Task.CompletedTask;
  }
}
