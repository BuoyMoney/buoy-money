BEGIN;

ALTER TABLE public."Transactions"
    DROP COLUMN IF EXISTS "TransferReviewedAt";

COMMIT;
