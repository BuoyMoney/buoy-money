-- Manual, reviewed rollback of 0049. Not auto-run.
-- buoy:reviewed-destructive: reverses an additive migration; nothing hosted reads these objects
DROP TABLE IF EXISTS public.registration_invites;
DROP TABLE IF EXISTS public.instance_settings;
ALTER TABLE public.user_device_tokens DROP COLUMN IF EXISTS public_key;
ALTER TABLE "Users" DROP COLUMN IF EXISTS "IsInstanceAdmin";
