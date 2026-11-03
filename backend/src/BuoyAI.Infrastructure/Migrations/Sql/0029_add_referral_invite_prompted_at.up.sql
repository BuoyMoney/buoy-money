-- Marks when we asked this user to invite someone.
--
-- The prompt is delivered by a periodic sweep, so without a durable marker every pass would
-- re-ask the same people. Sent once ever: one ignored invite prompt is a non-event, a
-- recurring one is a reason to unsubscribe.
--
-- NULL means not yet asked, correct for every existing user since nothing has ever sent this.
BEGIN;

ALTER TABLE public."Users"
    ADD COLUMN IF NOT EXISTS "ReferralInvitePromptedAt" timestamp with time zone NULL;

COMMIT;
