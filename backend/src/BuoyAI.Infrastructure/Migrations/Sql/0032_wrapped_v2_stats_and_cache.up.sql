-- Wrapped v2: aggregate percentile cutoffs + per-user bonus-card cache.
-- Cutoffs are aggregate-only (no per-user values); the cache pins a period's
-- AI/rotation bonus cards so stories are stable and the read path never
-- touches a model.

CREATE TABLE IF NOT EXISTS "WrappedStatCutoffs" (
    "MetricKey"  text PRIMARY KEY,
    "CutoffsJson" text NOT NULL DEFAULT '[]',
    "Cohort"     integer NOT NULL DEFAULT 0,
    "ComputedAt" timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS "WrappedBonusCardCaches" (
    "Id"          serial PRIMARY KEY,
    "UserId"      integer NOT NULL REFERENCES "Users"("Id") ON DELETE CASCADE,
    "Year"        integer NOT NULL,
    "Month"       integer NOT NULL,
    "CardsJson"   text NOT NULL DEFAULT '[]',
    "GeneratedAt" timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_WrappedBonusCardCaches_User_Period"
    ON "WrappedBonusCardCaches" ("UserId", "Year", "Month");
