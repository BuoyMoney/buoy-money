-- Manual, reviewed rollback for 0025. Never auto-run by DbUp.
-- Safe: the column is observability and debounce metadata only. Dropping it makes every
-- forced refresh behave as "never synced" (refresh on next critical action), which is
-- correct-but-chattier, not incorrect.
BEGIN;

ALTER TABLE public."Users" DROP COLUMN IF EXISTS "LastStripeSyncAt";

COMMIT;
