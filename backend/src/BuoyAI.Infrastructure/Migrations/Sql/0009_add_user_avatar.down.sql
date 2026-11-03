BEGIN;

ALTER TABLE public."Users"
    DROP COLUMN IF EXISTS "AvatarImage",
    DROP COLUMN IF EXISTS "AvatarContentType",
    DROP COLUMN IF EXISTS "AvatarUpdatedAt";

COMMIT;
