-- R3 Savings hardening: relax the BudgetTransfer -> SavingsGoal FKs from RESTRICT to SET NULL.
--
-- A user who transferred budget funds INTO a savings goal (BudgetTransfer.DestinationSavingsGoalId),
-- or resolved a category deficit BY PULLING from one (SourceSavingsGoalId), could never delete that
-- goal afterward: the ON DELETE RESTRICT FK rejected the delete, SavingsController only caught
-- ArgumentException, and the DbUpdateException surfaced as an HTTP 500 — the goal was permanently
-- stuck. Both goal-id columns are nullable and every transfer read already tolerates a null goal
-- (renders "Unknown"/omits the name), so SET NULL lets the delete succeed while the transfer's
-- financial audit row (amount, date, actor, budget) survives. Mirrors the transfer->category FKs
-- already relaxed in migration 0013.
--
-- Idempotent: guarded DROP/ADD only.
BEGIN;

ALTER TABLE public."BudgetTransfers"
    DROP CONSTRAINT IF EXISTS "FK_BudgetTransfers_SavingsGoals_SourceSavingsGoalId";
ALTER TABLE public."BudgetTransfers"
    ADD CONSTRAINT "FK_BudgetTransfers_SavingsGoals_SourceSavingsGoalId"
    FOREIGN KEY ("SourceSavingsGoalId") REFERENCES public."SavingsGoals"("Id") ON DELETE SET NULL;

ALTER TABLE public."BudgetTransfers"
    DROP CONSTRAINT IF EXISTS "FK_BudgetTransfers_SavingsGoals_DestinationSavingsGoalId";
ALTER TABLE public."BudgetTransfers"
    ADD CONSTRAINT "FK_BudgetTransfers_SavingsGoals_DestinationSavingsGoalId"
    FOREIGN KEY ("DestinationSavingsGoalId") REFERENCES public."SavingsGoals"("Id") ON DELETE SET NULL;

COMMIT;
