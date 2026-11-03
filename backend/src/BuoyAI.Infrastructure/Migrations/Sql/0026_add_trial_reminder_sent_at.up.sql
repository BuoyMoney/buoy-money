-- Marks when the mid-trial reminder email was sent to this user.
--
-- The reminder is delivered by a periodic sweep rather than a scheduled job per user, so the
-- sweep re-evaluates the same population on every pass. Without a durable marker it would
-- re-send to everyone still mid-trial on every run — a mailing loop, not a reminder.
--
-- NULL means "not yet sent", which is the correct starting state for every existing user:
-- nobody has ever received this email, since it had no caller until now. Existing mid-trial
-- users therefore get exactly one, on the first sweep after deploy.
--
-- Distinct from the trial-ENDING email, which is driven by Stripe's trial_will_end webhook a
-- few days before expiry. This one fires early in the trial to prompt activation.
BEGIN;

ALTER TABLE public."Users"
    ADD COLUMN IF NOT EXISTS "TrialReminderSentAt" timestamp with time zone NULL;

COMMIT;
