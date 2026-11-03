-- Manual rollback only (never auto-run).
ALTER TABLE public."AIUsageRecords"
    DROP COLUMN IF EXISTS "Provider",
    DROP COLUMN IF EXISTS "TokensEstimated";
