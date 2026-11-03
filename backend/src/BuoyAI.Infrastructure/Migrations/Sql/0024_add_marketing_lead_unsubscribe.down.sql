-- Manual, reviewed rollback for 0024. Never auto-run by DbUp.
-- Destructive: drops every recorded unsubscribe. Re-mailing addresses that opted out is a
-- CAN-SPAM violation, so export unsubscribed_at before running this and honour it by hand.
BEGIN;

DROP INDEX IF EXISTS public.ux_marketing_leads_unsubscribe_token;

ALTER TABLE public.marketing_leads
    DROP COLUMN IF EXISTS unsubscribed_at,
    DROP COLUMN IF EXISTS unsubscribe_token;

COMMIT;
