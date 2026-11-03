-- Manual rollback only (never auto-run).
-- buoy:reviewed-destructive: drops only the column 0055 added; the invite record is not needed by older releases.
ALTER TABLE public."Reservations"
    DROP COLUMN IF EXISTS "BetaInvitedAt";
