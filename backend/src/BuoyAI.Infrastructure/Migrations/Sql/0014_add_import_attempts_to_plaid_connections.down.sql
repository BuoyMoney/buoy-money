BEGIN;

ALTER TABLE public."PlaidConnections"
    DROP COLUMN IF EXISTS "ImportAttempts";

COMMIT;
