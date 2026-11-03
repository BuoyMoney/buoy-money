-- RFC #793 — converge existing Type=Personal family children onto the linked-category model.
--
-- Before this feature, a family member's personal budget was created as a Type=Personal child
-- (ParentBudgetId + AllocatedAmount set) but WITHOUT a linked family "allowance" category. The
-- allowance path, by contrast, always created a family BudgetCategory with
-- IsAllowance=true, LinkedBudgetId=<child>, AssignedUserId=<member>. This backfill gives those
-- pre-existing personal children the same linked family category so amount-binding, additive
-- member income, and personal-category bucketing all work for them too.
--
-- The new category parks on CategoryId 99 (Uncategorized) — the same convention the allowance
-- and "new family category" fund-from paths use — with its allocation seeded from the child's
-- current AllocatedAmount. Assigned to the child's owner (UserId).
--
-- Idempotent: only inserts where no linked IsAllowance category already exists for the child, so
-- re-running (or running after some children were already spawned via the new flow) is a no-op.
BEGIN;

INSERT INTO public."BudgetCategories"
    ("BudgetId", "CategoryId", "AllocatedAmount", "IsAllowance", "LinkedBudgetId", "AssignedUserId",
     "AllowRollover", "RolloverBalance", "CreatedAt", "UpdatedAt")
SELECT
    child."ParentBudgetId",
    99,                          -- Uncategorized (TransactionCategory.Uncategorized)
    child."AllocatedAmount",
    true,
    child."Id",
    child."UserId",
    false,
    0,
    CURRENT_TIMESTAMP,
    CURRENT_TIMESTAMP
FROM public."Budgets" child
WHERE child."Type" = 0               -- BudgetType.Personal
  AND child."ParentBudgetId" IS NOT NULL
  AND child."AllocatedAmount" IS NOT NULL
  AND NOT EXISTS (
      SELECT 1
      FROM public."BudgetCategories" bc
      WHERE bc."LinkedBudgetId" = child."Id"
        AND bc."IsAllowance" = true
  );

COMMIT;
