# SQL migrations (#533)

Hand-written, SQL-first migrations applied by [`SqlMigrationRunner`](../../Data/SqlMigrationRunner.cs)
via DbUp on app startup. This replaces EF Core's `MigrateAsync()` — no model snapshot, no
`.Designer.cs`, no silent-skip. The legacy EF `Migrations/*.cs` files were removed; they were
are **frozen** (never add new ones).

## Convention

Each migration is a pair, numbered in order:

```
NNNN_short_description.up.sql     ← applied forward by DbUp (embedded, journaled to schemaversions)
NNNN_short_description.down.sql   ← committed for manual, reviewed rollback (NOT auto-run)
```

- Only `*.up.sql` files are embedded (see the `.csproj` `EmbeddedResource` glob) and run on
  startup, in filename order. Keep the numeric prefix zero-padded and monotonic.
- `*.down.sql` is the reviewed reverse of the up script. We do **not** auto-run downs — rolling
  back production schema is a deliberate, supervised act. Apply a down by hand (psql) and delete
  the matching `schemaversions` row.
- Write plain, idempotent-friendly SQL where reasonable (`IF NOT EXISTS`, etc.).

## Transactions

The runner wraps every script in `BEGIN; ... COMMIT;`, so a script applies fully or not at all.
**Do not write your own BEGIN/COMMIT in a new script.** Scripts that cannot run inside a
transaction (`CREATE INDEX CONCURRENTLY`, `VACUUM`, `CREATE DATABASE`) opt out with a line:

```sql
-- buoy:no-transaction
CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_example ON "T"("C");
```

An opted-out script can stop half way, so write it to be safe to re-run. Scripts numbered 0047 and
below predate this: the 40 that open their own BEGIN run as written, and the 12 that did not are now
wrapped when they run on a fresh database. Journal names did not change, so databases that already
applied them do nothing.

## Rules the lint enforces (`MigrationLintTests`)

- Numbers are unique. Four pairs already shipped with a shared number (0013, 0014, 0015, 0021);
  they are listed in `MigrationLint.GrandfatheredDuplicates` and must not be renamed (hosted would
  re-run them). No other duplicate is allowed. The next free number is 0062 (0061 is Users.CloudOnly).
- Every `.up.sql` has a matching `.down.sql`.
- No `DROP TABLE/COLUMN/SCHEMA`, `TRUNCATE`, `DELETE` without `WHERE`, or `ALTER COLUMN ... TYPE`
  unless the script has a line `-- buoy:reviewed-destructive: <reason>`. Blue-green deploys keep the
  old version running on the new schema, so drop or retype only in a later release.
- Statements that cannot run in a transaction need the `-- buoy:no-transaction` marker.

Run `scripts/dev/migration-upgrade-check.sh` (needs Docker) to run the lint plus the upgrade tests
(empty to head, older schema to head, EF-managed baseline to head). CI runs the same in
`.github/workflows/migration-upgrade.yml`.

## Backup before migrating (self-host)

`Migrations:BackupBeforeMigrate=true` makes the runner take a `pg_dump` (custom format) into
`Migrations:BackupDirectory` before applying pending scripts to an existing database, then check it
with `pg_restore --list`. If the dump or the check fails the server refuses to migrate, unless
`Migrations:AllowMigrateWithoutBackup=true`. Defaults are off, so hosted behaviour is unchanged.
`PgDumpPath`, `PgRestorePath` and `KeepBackups` (default 5) are also settings. The `pg_dump` client
must be the same major version as the server or newer.

## Baseline

`0000_baseline.up.sql` is the full current schema, generated with
`pg_dump --schema-only` from a database that DbUp had already migrated and then post-processed so DbUp/Npgsql can run it:
- pg_dump's `\restrict` / `\unrestrict` backslash lines and `set_config('search_path'...)` are
  stripped (Npgsql's simple-query path can't parse them).
- `SET transaction_timeout = 0;` is stripped — it is a **PG17-only** GUC, and prod/staging/CI and
  local docker-compose all run **postgres:15**, where that line aborts the whole script.
- The schema body is wrapped in a single `BEGIN; … COMMIT;`. DbUp runs scripts `WithoutTransaction`,
  so the baseline must be atomic itself; otherwise a mid-script failure leaves a half-built,
  un-journaled schema that bricks every subsequent boot.

**Dump from the same major version the runtime uses (postgres:15)** so pg_dump v15 doesn't emit
PG17-only GUCs in the first place. Regenerate only when intentionally re-baselining:

```bash
# materialize the current schema into a throwaway DB by booting the API once (DbUp runs every
# *.up.sql on startup), then dump it (match runtime: PG15)
docker run -d --name buoy-baseline-pg -e POSTGRES_PASSWORD=pw -e POSTGRES_DB=baseline -p 55432:5432 postgres:15-alpine
Jwt__Key="<32+ chars>" ConnectionStrings__BuoyAI="Host=localhost;Port=55432;Username=postgres;Password=pw;Database=baseline" \
  dotnet run --project backend/src/BuoyAI.API --environment Development   # stop it once startup migrations finish
docker exec -e PGPASSWORD=pw buoy-baseline-pg pg_dump -U postgres --schema-only --no-owner --no-privileges baseline \
  | python -c 'import sys; sys.stdout.write("".join(l for l in sys.stdin if not l.lstrip().startswith("\\") and "set_config(\x27search_path\x27" not in l and not l.lstrip().startswith("SET transaction_timeout")))' \
  > backend/src/BuoyAI.Infrastructure/Migrations/Sql/0000_baseline.up.sql
docker rm -f buoy-baseline-pg
# Then wrap the schema body in one transaction: insert `BEGIN;` after the `SET` preamble (before the
# first CREATE) and `COMMIT;` at the very end — see the markers already in 0000_baseline.up.sql.
```

## Cutover behaviour (existing vs fresh DBs)

`SqlMigrationRunner` handles both:
- **Existing EF-managed DB** (prod/staging — has `__EFMigrationsHistory`, no `schemaversions`):
  the baseline is **pre-seeded as already-applied** and only newer migrations run.
- **Fresh DB** (new dev/local): the baseline runs in full, then newer migrations.
