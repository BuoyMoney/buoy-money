BEGIN;

ALTER TABLE public."Users"
    DROP COLUMN IF EXISTS "LastSubscriptionDetectionRunAt";

COMMIT;
