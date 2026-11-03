-- Tracks when we last projected this user's subscription state from Stripe.
--
-- Stripe is the authority for subscription state; the columns on Users are a projection of
-- it. This timestamp is what makes that projection safe to rely on:
--
--   * it debounces the forced refresh, so a user hammering a gated endpoint cannot turn one
--     paywall decision per request into one Stripe API call per request, and
--   * it makes staleness measurable. "Trial state last confirmed against Stripe at X" is an
--     answerable question, which it was not when the local row was a peer source of truth.
--
-- NULL means never synced, which is correct for every pre-existing user: their trial predates
-- Stripe-backed trials and has no subscription behind it. The projection deliberately leaves
-- those rows alone rather than treating "Stripe has nothing" as "revoke their trial".
BEGIN;

ALTER TABLE public."Users"
    ADD COLUMN IF NOT EXISTS "LastStripeSyncAt" timestamp with time zone NULL;

COMMIT;
