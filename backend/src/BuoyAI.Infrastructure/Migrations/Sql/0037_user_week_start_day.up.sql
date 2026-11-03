-- #941 week-start setting: 0=Sunday..6=Saturday (.NET DayOfWeek). NULL = legacy
-- behavior (Weekly starts Sunday, BiWeekly anchors Monday) so existing budgets
-- never shift; a set value anchors BOTH weekly and bi-weekly periods to it.
ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "WeekStartDay" smallint;
