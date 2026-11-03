-- Manual, reviewed rollback for 0043. Never auto-run by DbUp.
--
-- Not safe to run while the reminder sweep is live: this column is the only record that the
-- second email was already sent, so dropping it makes every eligible trial user receive it
-- again. Disable the sweep (Email:TrialReminder:Enabled = false) before rolling back.
BEGIN;

ALTER TABLE public."Users" DROP COLUMN IF EXISTS "TrialReminder2SentAt";

COMMIT;
