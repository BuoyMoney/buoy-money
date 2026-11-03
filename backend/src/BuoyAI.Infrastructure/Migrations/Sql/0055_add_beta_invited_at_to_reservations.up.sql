-- Closed beta (task 24): when a founding member was invited to the self-host beta, so each email is
-- invited once. Additive and nullable, safe while the previous release serves the same database.
ALTER TABLE public."Reservations"
    ADD COLUMN IF NOT EXISTS "BetaInvitedAt" timestamp with time zone NULL;
