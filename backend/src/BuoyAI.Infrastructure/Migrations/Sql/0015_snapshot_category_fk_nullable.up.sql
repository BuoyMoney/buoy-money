-- PR6: Budget period snapshots must survive category recreation.
--
-- BudgetPeriodCategorySnapshots.BudgetCategoryId was declared `integer NOT NULL`, but its FK to
-- BudgetCategories("Id") is ON DELETE SET NULL (see 0000_baseline). UpdateBudgetAsync edits a
-- budget by wiping and recreating its BudgetCategory rows, which makes EF DELETE the old category
-- rows. Once a budget has rolled over at least once (so period snapshots exist — normal for a
-- monthly budget), the SET NULL action fires against the NOT NULL BudgetCategoryId column and
-- Postgres aborts with `23502 null value in column "BudgetCategoryId" violates not-null
-- constraint`, making the budget permanently uneditable and making budget / account deletion
-- nondeterministically 500.
--
-- The snapshot already denormalizes CategoryId + CategoryName for historical reference, so a null
-- BudgetCategoryId (the link to the now-deleted live row) is harmless — the history survives.
--
-- Fix: drop NOT NULL so the SET NULL referential action can succeed. Idempotent: DROP NOT NULL is
-- a no-op when the column is already nullable.
BEGIN;

ALTER TABLE public."BudgetPeriodCategorySnapshots"
    ALTER COLUMN "BudgetCategoryId" DROP NOT NULL;

COMMIT;
