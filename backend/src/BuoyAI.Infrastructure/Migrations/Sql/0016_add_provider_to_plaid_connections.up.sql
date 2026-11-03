-- Multi-aggregator support (#310): tag each bank connection with the provider it came from.
--
-- BuoyAI started Plaid-only; the abstraction (IBankAggregator / IBankAggregatorRegistry) dispatches
-- ongoing sync + lifecycle work on this discriminator so multiple aggregators (MX, Teller, …) can
-- coexist in the same PlaidConnections table. Every pre-existing connection IS Plaid, so the column
-- defaults to 0 (BankProvider.Plaid) — which backfills existing rows correctly.
--
-- Idempotent: ADD COLUMN IF NOT EXISTS is a no-op if the column already exists.
BEGIN;

ALTER TABLE public."PlaidConnections"
    ADD COLUMN IF NOT EXISTS "Provider" integer NOT NULL DEFAULT 0;

COMMIT;
