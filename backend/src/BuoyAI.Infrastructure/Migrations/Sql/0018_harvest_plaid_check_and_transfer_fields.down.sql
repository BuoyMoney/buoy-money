-- Drops harvested Plaid fields. Recoverable: a re-sync refetches them from Plaid.
BEGIN;

ALTER TABLE public."Transactions"
    DROP COLUMN IF EXISTS "CheckNumber",
    DROP COLUMN IF EXISTS "TransactionCode",
    DROP COLUMN IF EXISTS "PaymentPayee",
    DROP COLUMN IF EXISTS "PaymentPayer",
    DROP COLUMN IF EXISTS "PaymentReason",
    DROP COLUMN IF EXISTS "PaymentReferenceNumber",
    DROP COLUMN IF EXISTS "AuthorizedDate",
    DROP COLUMN IF EXISTS "IsoCurrencyCode";

COMMIT;
