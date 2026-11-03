-- Let the user rename a transaction.
--
-- Plaid leaves MerchantName null for checks and account transfers, and only sends
-- OriginalDescription when include_original_description is set (we don't), so those rows
-- historically landed with no name at all — ~19% of one real user's transactions. The ingest
-- now falls back to Plaid's always-present `name`, but that can still be a bank code the user
-- can't read, and it can't recover rows already stored blank.
--
-- Stored separately from Description rather than overwriting it: Description holds the bank's
-- own text, and we keep no other copy of it (OriginalDescription is always null), so an
-- in-place rename would destroy it irrecoverably. It would also be clobbered when a pending
-- hold is promoted to its posted charge. This mirrors Accounts."Nickname" — the same
-- user-overlay-on-bank-data pattern, with the same DisplayName precedence.
--
-- Idempotent: guarded ADD COLUMN only.
BEGIN;

ALTER TABLE public."Transactions"
    ADD COLUMN IF NOT EXISTS "Nickname" text;

COMMIT;
