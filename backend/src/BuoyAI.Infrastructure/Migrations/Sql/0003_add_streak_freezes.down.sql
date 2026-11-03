BEGIN;
ALTER TABLE public."UserStreaks" DROP COLUMN IF EXISTS "StreakFreezes";
COMMIT;
