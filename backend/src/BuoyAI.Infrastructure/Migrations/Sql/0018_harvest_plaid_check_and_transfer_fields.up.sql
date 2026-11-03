-- Harvest the Plaid transaction fields we were dropping.
--
-- Plaid names a transaction through merchant_name, and leaves it null for exactly two kinds:
-- checks and inter-bank transfers. Those are the rows that land nameless (~19% of one real
-- user's transactions). Plaid does describe them — via check_number and payment_meta — we just
-- never asked. All of this already ships in the /transactions/get payload under the Transactions
-- product, so there is no extra product, consent, or cost.
--
-- Every column here has a consumer (the naming fallback, the categorizer prompt, or the detail
-- sheet). We just learned what a write-only column costs: OriginalDescription sat null on 716/716
-- rows because nothing ever requested it.
--
-- Idempotent: guarded ADD COLUMN only.
BEGIN;

ALTER TABLE public."Transactions"
    ADD COLUMN IF NOT EXISTS "CheckNumber" text,
    ADD COLUMN IF NOT EXISTS "TransactionCode" text,
    ADD COLUMN IF NOT EXISTS "PaymentPayee" text,
    ADD COLUMN IF NOT EXISTS "PaymentPayer" text,
    ADD COLUMN IF NOT EXISTS "PaymentReason" text,
    ADD COLUMN IF NOT EXISTS "PaymentReferenceNumber" text,
    ADD COLUMN IF NOT EXISTS "AuthorizedDate" timestamp with time zone,
    ADD COLUMN IF NOT EXISTS "IsoCurrencyCode" text;

COMMIT;
