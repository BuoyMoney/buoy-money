-- RFC: auto-remove Plaid items for churned accounts.
-- Durable marker that the churn sweep has revoked this Item at Plaid (/item/remove),
-- so it is no longer billable and the nightly sweep skips it (idempotency). Distinct
-- from "IsActive", which is overloaded across login-required / permission-revoked /
-- locally-disconnected states and cannot say whether the Plaid Item itself was removed.
ALTER TABLE public."PlaidConnections"
    ADD COLUMN IF NOT EXISTS "PlaidRemovedAt" timestamp with time zone NULL;
