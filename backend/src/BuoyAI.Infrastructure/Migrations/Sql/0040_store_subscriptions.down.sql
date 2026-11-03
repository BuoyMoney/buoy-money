DROP TABLE IF EXISTS "ProcessedRevenueCatEvents";
DROP INDEX IF EXISTS "IX_UserSubscriptions_Provider_ExternalId";
ALTER TABLE "UserSubscriptions" DROP COLUMN IF EXISTS "ExternalId";
ALTER TABLE "UserSubscriptions" DROP COLUMN IF EXISTS "Provider";
