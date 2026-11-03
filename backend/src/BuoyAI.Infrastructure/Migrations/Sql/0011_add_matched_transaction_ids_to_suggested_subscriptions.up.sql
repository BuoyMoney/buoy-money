-- Persist the transaction IDs that matched a detected subscription pattern (CSV of ints).
-- Confirming a suggestion links the real Subscription to exactly these transactions so
-- billing history / LastBilledDate are populated without re-running detection.
BEGIN;

ALTER TABLE public."SuggestedSubscriptions"
    ADD COLUMN IF NOT EXISTS "MatchedTransactionIds" character varying(4000);

COMMIT;
