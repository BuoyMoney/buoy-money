-- Idempotency ledger for the offline-sync batch endpoint (#493). One row per
-- (user_id, key) client-generated operation key. POST /api/sync/batch records each
-- operation's final status after processing and short-circuits any key already present,
-- so a replayed outbox returns the original outcome instead of re-applying the mutation.
-- See SyncService.ProcessBatchAsync / SyncIdempotencyKey. Rows are opportunistically
-- pruned after ~48h on each batch call.
--
-- Idempotent: CREATE TABLE / INDEX IF NOT EXISTS are no-ops if they already exist.
BEGIN;

CREATE TABLE IF NOT EXISTS public.sync_idempotency_keys (
    user_id integer NOT NULL,
    key text NOT NULL,
    operation_type text NOT NULL,
    status text NOT NULL,
    processed_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT pk_sync_idempotency_keys PRIMARY KEY (user_id, key)
);

-- Supports the opportunistic retention delete (processed_at < now - 48h).
CREATE INDEX IF NOT EXISTS ix_sync_idempotency_keys_processed_at
    ON public.sync_idempotency_keys (processed_at);

COMMIT;
