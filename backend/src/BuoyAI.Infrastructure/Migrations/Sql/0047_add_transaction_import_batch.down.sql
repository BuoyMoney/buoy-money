-- Manual, reviewed rollback for 0047. Never auto-run by DbUp.
--
-- Dropping the column removes the ability to undo past imports; deploy code that no longer
-- reads "ImportBatchId" before running this.
BEGIN;

DROP INDEX IF EXISTS public."IX_Transactions_ImportBatchId";
ALTER TABLE public."Transactions" DROP COLUMN IF EXISTS "ImportBatchId";

COMMIT;
