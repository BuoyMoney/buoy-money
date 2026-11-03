-- Idempotency ledger for Stripe webhook events. The webhook handler records each
-- Stripe event id (evt_...) after it processes successfully and skips any id already
-- present, so redeliveries / at-least-once duplicates never re-run handlers (which
-- would double-insert SubscriptionPayment rows and re-send emails). See
-- StripeWebhookController / BillingService.MarkStripeEventProcessedAsync.
BEGIN;

CREATE TABLE IF NOT EXISTS public."ProcessedStripeEvents" (
    "EventId" character varying(255) NOT NULL,
    "EventType" character varying(100) NOT NULL,
    "ProcessedAt" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT "PK_ProcessedStripeEvents" PRIMARY KEY ("EventId")
);

COMMIT;
