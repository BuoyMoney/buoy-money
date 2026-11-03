-- Manual rollback only. The cleared values were stale copies of amount / period days; the
-- closest restoration recomputes them from the current period.
UPDATE "BudgetCategories" bc
SET "DailyAllowance" = ROUND(bc."AllocatedAmount" / ((b."CurrentPeriodEnd"::date - b."CurrentPeriodStart"::date) + 1), 2)
FROM "Budgets" b
WHERE b."Id" = bc."BudgetId" AND bc."IsCountUpEnabled" = true AND bc."DailyAllowance" = 0;
