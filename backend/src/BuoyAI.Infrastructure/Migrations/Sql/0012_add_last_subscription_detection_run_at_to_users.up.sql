-- Reliable last-run timestamp for the on-demand subscription-detection cooldown.
-- Replaces inferring "did detection run recently?" from suggestion DetectedAt, which lost the
-- signal whenever a run produced zero suggestions and let users hammer POST /subscriptions/detect.
BEGIN;

ALTER TABLE public."Users"
    ADD COLUMN IF NOT EXISTS "LastSubscriptionDetectionRunAt" timestamp with time zone;

COMMIT;
