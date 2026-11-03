-- R4 Transfer-detection hardening: track "dismissed as not-a-transfer" separately from categorization.
--
-- Dismissing a suspected transfer used to re-apply the current category through the normal
-- category-update path, which unconditionally sets IsUserCategorized = true / NeedsReview = false.
-- That pulled an otherwise-Uncategorized transaction out of the AI "needs review" queue even though
-- the user only said "not a transfer", not "correctly categorized". A dedicated timestamp lets the
-- transfer detector's eligibility filter skip dismissed rows WITHOUT overloading IsUserCategorized,
-- so the transaction stays in the normal categorization flow.
--
-- Idempotent: guarded ADD COLUMN only.
BEGIN;

ALTER TABLE public."Transactions"
    ADD COLUMN IF NOT EXISTS "TransferReviewedAt" timestamp with time zone;

COMMIT;
