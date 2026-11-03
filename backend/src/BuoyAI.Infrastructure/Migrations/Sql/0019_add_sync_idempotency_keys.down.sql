-- Reverse of 0019_add_sync_idempotency_keys.up.sql (#493).
-- Manual, reviewed rollback only — DbUp never auto-runs down scripts. Dropping this table
-- loses in-flight idempotency records; a client mid-retry could then re-apply an operation.
BEGIN;

DROP TABLE IF EXISTS public.sync_idempotency_keys;

COMMIT;
