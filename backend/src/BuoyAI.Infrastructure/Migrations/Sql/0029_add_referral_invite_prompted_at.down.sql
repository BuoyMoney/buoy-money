-- Manual, reviewed rollback for 0029. Never auto-run by DbUp.
--
-- NOT safe while the sweep is live: this column is the only record that someone was already
-- asked, so dropping it makes every activated user eligible again and the next pass re-mails
-- all of them. Disable the sweep (Referral:InvitePrompt:Enabled = false) first.
BEGIN;

ALTER TABLE public."Users" DROP COLUMN IF EXISTS "ReferralInvitePromptedAt";

COMMIT;
