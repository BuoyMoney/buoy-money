BEGIN;

ALTER TABLE public."SuggestedSubscriptions"
    DROP COLUMN IF EXISTS "MatchedTransactionIds";

COMMIT;
