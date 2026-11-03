-- Rollback for 0016: drop the aggregator provider discriminator.
-- (Manual rollback only — .down.sql is not embedded/auto-run; see Migrations/Sql/README.md.)
BEGIN;

ALTER TABLE public."PlaidConnections"
    DROP COLUMN IF EXISTS "Provider";

COMMIT;
