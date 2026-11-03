-- 0001 (DOWN) — reverse of the admin-broadcast migration (#552). Manual, reviewed rollback only.
BEGIN;

DROP TABLE IF EXISTS public."BroadcastRecipients";
DROP TABLE IF EXISTS public."Broadcasts";

ALTER TABLE public.user_email_preferences
    DROP COLUMN IF EXISTS "ProductAnnouncements";

COMMIT;
