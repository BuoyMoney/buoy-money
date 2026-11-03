-- Manual rollback only (never auto-run).
ALTER TABLE public."Users"
    DROP COLUMN IF EXISTS "CloudOnly";
