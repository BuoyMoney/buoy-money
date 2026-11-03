-- Repeatable ("oops") achievements: count earns, survive revocation, unlock meta-badges.
ALTER TABLE "UserAchievements" ADD COLUMN IF NOT EXISTS "TimesEarned" integer NOT NULL DEFAULT 1;
ALTER TABLE "UserAchievements" ADD COLUMN IF NOT EXISTS "LastEarnedAt" timestamptz NULL;
ALTER TABLE "UserAchievements" ADD COLUMN IF NOT EXISTS "RevokedAt" timestamptz NULL;
UPDATE "UserAchievements" SET "LastEarnedAt" = "EarnedAt" WHERE "LastEarnedAt" IS NULL;
