-- Reverse of 0014_transfer_savings_goal_fk_setnull.up.sql.
-- Restores ON DELETE RESTRICT on the transfer -> savings-goal FKs. Rows whose goal id was already
-- nulled by a SET NULL cascade are NOT restored (not reversible); this only restores the schema.
BEGIN;

ALTER TABLE public."BudgetTransfers"
    DROP CONSTRAINT IF EXISTS "FK_BudgetTransfers_SavingsGoals_SourceSavingsGoalId";
ALTER TABLE public."BudgetTransfers"
    ADD CONSTRAINT "FK_BudgetTransfers_SavingsGoals_SourceSavingsGoalId"
    FOREIGN KEY ("SourceSavingsGoalId") REFERENCES public."SavingsGoals"("Id") ON DELETE RESTRICT;

ALTER TABLE public."BudgetTransfers"
    DROP CONSTRAINT IF EXISTS "FK_BudgetTransfers_SavingsGoals_DestinationSavingsGoalId";
ALTER TABLE public."BudgetTransfers"
    ADD CONSTRAINT "FK_BudgetTransfers_SavingsGoals_DestinationSavingsGoalId"
    FOREIGN KEY ("DestinationSavingsGoalId") REFERENCES public."SavingsGoals"("Id") ON DELETE RESTRICT;

COMMIT;
