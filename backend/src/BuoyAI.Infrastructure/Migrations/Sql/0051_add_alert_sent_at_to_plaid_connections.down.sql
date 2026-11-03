-- Manual rollback only (never auto-run).
ALTER TABLE public."PlaidConnections"
    DROP COLUMN IF EXISTS "AlertSentAt";
