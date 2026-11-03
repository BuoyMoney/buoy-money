-- Defense-in-depth against double-billing: a user must have at most one *live* billing
-- subscription at a time. The application guards (BillingService: CreateCheckoutSessionAsync,
-- CreateSubscriptionAsync, ActivateSubscriptionFromCheckoutAsync) block a second checkout
-- while the first is Active OR PastDue, but a partial unique index makes a duplicate insert
-- impossible even under a race. "Live" = Active(0), PastDue(1), Trialing(4); Canceled(2),
-- Expired(3), TrialExpired(5) are terminal and may repeat per user.
--
-- The index is created only when no user currently has multiple live rows, so this never
-- fails a deploy on data that predates the fix (the app-level guards are the real fix;
-- legacy duplicates must be reconciled by hand before the index can be added). Idempotent.
-- DbUp runs scripts WithoutTransaction; this script needs none.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM public."UserSubscriptions"
        WHERE "Status" IN (0, 1, 4)
        GROUP BY "UserId"
        HAVING COUNT(*) > 1
    ) THEN
        RAISE NOTICE 'Skipping IX_UserSubscriptions_UserId_Live: some users already have multiple live subscriptions; reconcile duplicates then re-run.';
    ELSE
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_UserSubscriptions_UserId_Live"
            ON public."UserSubscriptions" ("UserId")
            WHERE "Status" IN (0, 1, 4);
    END IF;
END
$$;
