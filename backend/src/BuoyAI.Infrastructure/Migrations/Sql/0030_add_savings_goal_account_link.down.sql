-- Manual, reviewed rollback only — never auto-run.
--
-- Dropping the column discards which account each goal was reconciling against. No balance is
-- lost (that lives in "CurrentAmount"), but the links themselves cannot be recovered.
BEGIN;

DROP INDEX IF EXISTS public."IX_SavingsGoals_AccountId";

ALTER TABLE public."SavingsGoals"
    DROP CONSTRAINT IF EXISTS "FK_SavingsGoals_Accounts_AccountId";

ALTER TABLE public."SavingsGoals"
    DROP COLUMN IF EXISTS "AccountId";

COMMIT;
