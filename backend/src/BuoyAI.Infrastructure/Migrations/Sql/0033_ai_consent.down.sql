-- Manual rollback only (never auto-run).
ALTER TABLE "Users" DROP COLUMN IF EXISTS "AiConsentWithdrawnAt";
ALTER TABLE "Users" DROP COLUMN IF EXISTS "AiConsentGrantedAt";
