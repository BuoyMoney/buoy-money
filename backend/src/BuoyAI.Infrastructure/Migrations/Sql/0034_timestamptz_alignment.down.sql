-- Manual rollback only (never auto-run).
ALTER TABLE "Users" ALTER COLUMN "AiConsentGrantedAt" TYPE timestamp USING "AiConsentGrantedAt" AT TIME ZONE 'UTC';
ALTER TABLE "Users" ALTER COLUMN "AiConsentWithdrawnAt" TYPE timestamp USING "AiConsentWithdrawnAt" AT TIME ZONE 'UTC';
ALTER TABLE "WrappedStatCutoffs" ALTER COLUMN "ComputedAt" TYPE timestamp USING "ComputedAt" AT TIME ZONE 'UTC';
ALTER TABLE "WrappedBonusCardCaches" ALTER COLUMN "GeneratedAt" TYPE timestamp USING "GeneratedAt" AT TIME ZONE 'UTC';
