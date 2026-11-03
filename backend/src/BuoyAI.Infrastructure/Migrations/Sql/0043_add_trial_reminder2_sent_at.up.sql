-- Marks when the SECOND connect-your-bank email (the day-4 "what stopped you?" note) was
-- sent. The first email keeps using "TrialReminderSentAt" from 0026.
--
-- Same reason as 0026: the connect reminders are delivered by a periodic sweep that
-- re-evaluates the same population on every pass, so a durable marker is what keeps this a
-- single email instead of a mailing loop.
--
-- NULL is the correct starting state for every existing user. Anyone still mid-trial who
-- already got the first email and never connected a bank receives the second one once, on
-- the first sweep after deploy.
BEGIN;

ALTER TABLE public."Users"
    ADD COLUMN IF NOT EXISTS "TrialReminder2SentAt" timestamp with time zone NULL;

COMMIT;
