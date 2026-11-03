-- Manual, reviewed rollback only — never auto-run.
--
-- Dropping the column loses the record of who has already been reminded. Re-applying
-- the forward migration afterwards would leave every unredeemed lead looking un-reminded,
-- so the sweep would email them a second time. Prefer disabling the sweep by config over
-- rolling this back on a live database.
BEGIN;

DROP INDEX IF EXISTS public.ix_marketing_leads_pending_reminder;

ALTER TABLE public.marketing_leads
    DROP COLUMN IF EXISTS reminder_sent_at;

COMMIT;
