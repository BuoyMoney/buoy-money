-- Cloud-only accounts: people who signed up on the Cloud portal (buoy-ai.com/cloud/) to buy add-ons
-- for their own server. They get a Stripe customer but no hosted trial and no hosted onboarding.
-- Additive (new column with a default), safe while the old version still serves the same database.
ALTER TABLE public."Users"
    ADD COLUMN IF NOT EXISTS "CloudOnly" boolean NOT NULL DEFAULT false;
