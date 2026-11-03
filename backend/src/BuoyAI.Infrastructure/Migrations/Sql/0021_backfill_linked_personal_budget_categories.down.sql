-- Reverse of 0021: drop the linked family "allowance" categories created to back existing
-- Type=Personal children. Only removes the Uncategorized (99) linkage rows that fund a
-- Type=Personal child — leaves genuine Type=Allowance linkages untouched.
--
-- NOTE: not auto-run. Apply by hand (psql) and delete the matching schemaversions row.
BEGIN;

DELETE FROM public."BudgetCategories" bc
USING public."Budgets" child
WHERE bc."LinkedBudgetId" = child."Id"
  AND bc."IsAllowance" = true
  AND bc."CategoryId" = 99
  AND child."Type" = 0;              -- BudgetType.Personal

COMMIT;
