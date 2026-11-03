-- Rollback for 0021 (manual, reviewed — never auto-run).
BEGIN;

DROP TABLE IF EXISTS public.queued_push_notifications;
DROP TABLE IF EXISTS public.user_push_preferences;
DROP TABLE IF EXISTS public.user_device_tokens;

COMMIT;
