-- Manual, reviewed rollback for 0044. Never auto-run by DbUp.
--
-- Dropping the column makes the code unable to tell current grants from stale ones; deploy
-- code that no longer reads "AiConsentVersion" before running this.
BEGIN;

ALTER TABLE public."Users" DROP COLUMN IF EXISTS "AiConsentVersion";

COMMIT;
