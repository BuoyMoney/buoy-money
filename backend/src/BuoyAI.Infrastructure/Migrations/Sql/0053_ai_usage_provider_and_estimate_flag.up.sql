-- Metering fixes (task 13): say which provider a usage row came from, and whether its token counts
-- were the provider's own or a guess. Rows written before this change split one token total 70/30 into
-- input and output, so they are all guesses; that is what the default records. New rows carry the
-- provider's real counts and set TokensEstimated to false.
ALTER TABLE public."AIUsageRecords"
    ADD COLUMN IF NOT EXISTS "Provider" character varying(30) NULL,
    ADD COLUMN IF NOT EXISTS "TokensEstimated" boolean NOT NULL DEFAULT true;
