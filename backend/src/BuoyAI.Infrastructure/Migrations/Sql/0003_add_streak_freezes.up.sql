-- Earnable streak-freeze: bank freezes that auto-cover a missed day so one slip doesn't
-- reset a long streak (loss-aversion mitigation).
BEGIN;
ALTER TABLE public."UserStreaks"
    ADD COLUMN IF NOT EXISTS "StreakFreezes" integer NOT NULL DEFAULT 0;
COMMIT;
