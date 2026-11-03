-- Manual, reviewed rollback for 0026. Never auto-run by DbUp.
--
-- NOT safe to run casually while the reminder sweep is live: this column is the only record
-- that the reminder was already sent, so dropping it makes every current trial eligible
-- again and the next sweep re-mails all of them. Disable the sweep
-- (Email:TrialReminder:Enabled = false) before rolling back.
BEGIN;

ALTER TABLE public."Users" DROP COLUMN IF EXISTS "TrialReminderSentAt";

COMMIT;
