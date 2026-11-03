-- Bounded retry counter for the Plaid historical import.
-- Lets the background importer requeue Failed / stale-InProgress imports (which were previously
-- orphaned forever after a mid-import redeploy, OOM kill, or transient Plaid error) while
-- capping the number of automatic retries so a genuinely broken connection can't loop endlessly.
BEGIN;

ALTER TABLE public."PlaidConnections"
    ADD COLUMN IF NOT EXISTS "ImportAttempts" integer NOT NULL DEFAULT 0;

COMMIT;
