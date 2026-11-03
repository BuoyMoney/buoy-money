-- Marks when a captured lead was reminded that their reserved trial is still waiting.
--
-- Capture sends exactly one confirmation and then nothing ever again, so a visitor who
-- gave us their email but did not register that day was never contacted a second time.
-- This column is what stops the reminder sweep re-sending on every pass.
--
-- NULL means not yet reminded, correct for every existing row since nothing has ever
-- sent this.
BEGIN;

ALTER TABLE public.marketing_leads
    ADD COLUMN IF NOT EXISTS reminder_sent_at timestamp with time zone NULL;

-- The sweep looks for leads that are unredeemed, still subscribed, and not yet
-- reminded. Partial index so it stays small: rows drop out of it permanently once
-- reminded, which is the overwhelming majority over time.
CREATE INDEX IF NOT EXISTS ix_marketing_leads_pending_reminder
    ON public.marketing_leads (created_at)
    WHERE reminder_sent_at IS NULL
      AND redeemed_at IS NULL
      AND unsubscribed_at IS NULL;

COMMIT;
