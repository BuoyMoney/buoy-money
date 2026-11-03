-- Its own opt-out for referral invite prompts.
--
-- Deliberately not folded into ProductUpdates: an invite nag is the most irritating item in
-- the list to someone with no interest in referring anyone, and sharing a column would force
-- them to mute genuine product news to escape it.
--
-- Defaults ON to match the posture set in 0027, and existing rows are brought with it for the
-- same reason 0027 backfilled: changing an entity default alone reaches nobody who has
-- already signed up.
BEGIN;

ALTER TABLE public.user_email_preferences
    ADD COLUMN IF NOT EXISTS "ReferralInvites" boolean NOT NULL DEFAULT true;

COMMIT;
