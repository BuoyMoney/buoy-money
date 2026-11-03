-- Optional link from a savings goal to the real account its money lives in.
--
-- Reconciliation only. "CurrentAmount" on the goal stays the source of truth — it is an envelope
-- balance moved by contributions, withdrawals and budget-surplus allocations. The link exists so
-- the app can notice when the envelope total and the actual account balance have drifted apart,
-- which today it cannot see at all.
--
-- NULL is the correct and expected state for every existing goal, and stays a first-class choice:
-- saving inside a checking account, in cash, or toward something with no account yet.
--
-- ON DELETE SET NULL, deliberately: unlinking an account (closing it, removing a Plaid item) must
-- never cascade into deleting the goal and the saved balance it represents.
BEGIN;

ALTER TABLE public."SavingsGoals"
    ADD COLUMN IF NOT EXISTS "AccountId" integer NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'FK_SavingsGoals_Accounts_AccountId'
    ) THEN
        ALTER TABLE public."SavingsGoals"
            ADD CONSTRAINT "FK_SavingsGoals_Accounts_AccountId"
            FOREIGN KEY ("AccountId") REFERENCES public."Accounts" ("Id") ON DELETE SET NULL;
    END IF;
END $$;

-- Every read filters goals by account, so index the FK.
CREATE INDEX IF NOT EXISTS "IX_SavingsGoals_AccountId"
    ON public."SavingsGoals" ("AccountId");

COMMIT;
