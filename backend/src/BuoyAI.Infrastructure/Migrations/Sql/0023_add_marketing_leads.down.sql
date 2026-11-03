-- Manual, reviewed rollback for 0023. Never auto-run by DbUp.
-- Destructive: drops every captured marketing lead, including unredeemed trial offers
-- that visitors were promised. Export the table before running this.
BEGIN;

DROP INDEX IF EXISTS public.ix_marketing_leads_confirmation_pending;
DROP INDEX IF EXISTS public.ix_marketing_leads_unredeemed;
DROP INDEX IF EXISTS public.ux_marketing_leads_email;
DROP TABLE IF EXISTS public.marketing_leads;

COMMIT;
