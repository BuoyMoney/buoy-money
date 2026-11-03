-- In-app purchases through RevenueCat (App Store now, Play later). A UserSubscriptions row can
-- now come from a store instead of Stripe: Provider says which, ExternalId is "<store>:<product>"
-- so webhook events and the REST subscriber lookup land on the same row.
ALTER TABLE "UserSubscriptions" ADD COLUMN IF NOT EXISTS "Provider" smallint NOT NULL DEFAULT 0;
ALTER TABLE "UserSubscriptions" ADD COLUMN IF NOT EXISTS "ExternalId" varchar(255) NULL;
CREATE INDEX IF NOT EXISTS "IX_UserSubscriptions_Provider_ExternalId"
  ON "UserSubscriptions" ("Provider", "ExternalId") WHERE "ExternalId" IS NOT NULL;

-- Webhook idempotency ledger, the RevenueCat twin of ProcessedStripeEvents.
CREATE TABLE IF NOT EXISTS "ProcessedRevenueCatEvents" (
  "EventId" varchar(255) PRIMARY KEY,
  "EventType" varchar(100) NOT NULL DEFAULT '',
  "ProcessedAt" timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);
