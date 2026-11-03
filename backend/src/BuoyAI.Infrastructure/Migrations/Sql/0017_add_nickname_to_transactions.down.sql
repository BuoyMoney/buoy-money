-- Drops user-entered transaction names. Not recoverable — Nickname is the only copy.
BEGIN;

ALTER TABLE public."Transactions"
    DROP COLUMN IF EXISTS "Nickname";

COMMIT;
