-- Manual, reviewed rollback for 0028. Never auto-run by DbUp.
-- Safe: dropping the column removes the opt-out, so invite prompts fall back to always-on.
-- Disable the invite sweep (Referral:InvitePrompt:Enabled = false) before rolling back, or
-- users who opted out will start receiving prompts again.
BEGIN;

ALTER TABLE public.user_email_preferences DROP COLUMN IF EXISTS "ReferralInvites";

COMMIT;
