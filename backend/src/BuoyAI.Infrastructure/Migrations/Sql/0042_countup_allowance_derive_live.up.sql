-- Count-up categories stored a frozen copy of the daily allowance (amount / days in the period
-- at the moment count-up was switched on) and never updated it, so changing the category amount,
-- or moving from a 31-day month into February, left the allowance wrong. The app now leaves
-- DailyAllowance at 0 and CountUpBudgetService derives it on every read. Clear the frozen copies.
-- No screen sets a custom allowance (the countup-settings endpoint is unused by the app), so
-- every non-zero value here is one of those stale copies.
UPDATE "BudgetCategories"
SET "DailyAllowance" = 0
WHERE "IsCountUpEnabled" = true AND "DailyAllowance" <> 0;
