-- Unsubscribe support for marketing leads.
--
-- A lead has no account, so the existing UserEmailPreferences table (keyed on a user) cannot
-- hold their choice. Without this there is no way for someone who gave us an address to stop
-- hearing from us, which the popup's own copy promises and which CAN-SPAM requires the moment
-- these mails stop being purely transactional.
--
-- unsubscribe_token is a random opaque value, not a hash of the email: it goes in a URL, and a
-- derived value would let anyone unsubscribe (or confirm the existence of) an arbitrary address
-- by computing it. Random + unique means the link only works for whoever received the email,
-- and it can be rotated by issuing a new one.
--
-- Backfills existing rows so leads captured before this migration are also unsubscribable.
BEGIN;

ALTER TABLE public.marketing_leads
    ADD COLUMN IF NOT EXISTS unsubscribe_token character varying(64) NULL,
    ADD COLUMN IF NOT EXISTS unsubscribed_at timestamp with time zone NULL;

-- gen_random_uuid() is built in from PG13; hex-stripped to match the app's token format.
UPDATE public.marketing_leads
SET unsubscribe_token = replace(gen_random_uuid()::text, '-', '')
WHERE unsubscribe_token IS NULL;

ALTER TABLE public.marketing_leads
    ALTER COLUMN unsubscribe_token SET NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_marketing_leads_unsubscribe_token
    ON public.marketing_leads (unsubscribe_token);

COMMIT;
