-- Tags each transaction created by a file import with the batch that created it, so the
-- import screen can offer "Undo this import" for a day after it runs. NULL for everything
-- else (Plaid, manual entry, older imports).
BEGIN;

ALTER TABLE public."Transactions"
    ADD COLUMN IF NOT EXISTS "ImportBatchId" character varying(40) NULL;

CREATE INDEX IF NOT EXISTS "IX_Transactions_ImportBatchId"
    ON public."Transactions" ("ImportBatchId")
    WHERE "ImportBatchId" IS NOT NULL;

COMMIT;
