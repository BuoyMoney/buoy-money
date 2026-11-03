-- Unify gamification: the separate SavingsAchievements store is replaced by the single
-- UserAchievements system (savings achievements are awarded/read via GamificationService).
-- No writers or readers remain. Safe to drop (pre-launch, no production users).
BEGIN;
DROP TABLE IF EXISTS public."SavingsAchievements";
COMMIT;
