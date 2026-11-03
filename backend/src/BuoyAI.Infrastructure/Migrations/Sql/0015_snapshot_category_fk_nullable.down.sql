-- Reverse 0015: restore NOT NULL on BudgetPeriodCategorySnapshots.BudgetCategoryId.
--
-- Any snapshot whose live category was deleted (BudgetCategoryId now NULL via the SET NULL FK)
-- would block re-adding NOT NULL. Those rows are orphaned history for a category that no longer
-- exists, so drop them first. Rolling back this migration is not expected in normal operation.
BEGIN;

DELETE FROM public."BudgetPeriodCategorySnapshots" WHERE "BudgetCategoryId" IS NULL;

ALTER TABLE public."BudgetPeriodCategorySnapshots"
    ALTER COLUMN "BudgetCategoryId" SET NOT NULL;

COMMIT;
