-- Store an uploaded profile picture (small, client-resized) directly in the DB.
-- Distinct from the Google-provided ProfilePictureUrl. Null = fall back to initials.
-- Served via GET /api/user/avatar/{id}; set on POST, cleared on DELETE.
BEGIN;

ALTER TABLE public."Users"
    ADD COLUMN IF NOT EXISTS "AvatarImage" bytea,
    ADD COLUMN IF NOT EXISTS "AvatarContentType" text,
    ADD COLUMN IF NOT EXISTS "AvatarUpdatedAt" timestamptz;

COMMIT;
