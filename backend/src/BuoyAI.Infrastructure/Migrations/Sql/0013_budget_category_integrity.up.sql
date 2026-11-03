-- PR5: Budget data-model integrity (unique index + transfer cascade).
--
-- (1) DUPLICATE BUDGET CATEGORIES.
--     The old unique index (BudgetId, CategoryId, AssignedUserId) treated NULL as distinct
--     (Postgres default), so two rows for the SAME category with a NULL AssignedUserId (or a
--     NULL CategoryId) slipped through. Duplicates double-count spend on the budget page and
--     make the NEXT UpdateBudgetAsync throw (ToDictionary on a duplicate CategoryId key -> 500,
--     leaving the budget permanently uneditable). Fix: de-dupe any existing offending rows, then
--     replace the index with a COALESCE-based one so NULLs collapse to a 0 sentinel (matching the
--     app's GetEffectiveCategoryId semantics) and can no longer be duplicated.
--
-- (2) TRANSFER -> CATEGORY FK BLOCKED BUDGET DELETION.
--     BudgetTransfers -> BudgetCategories used ON DELETE RESTRICT, so deleting (or editing) a
--     budget that had any fund-transfer history 500'd on the FK when the cascade tried to remove
--     the referenced categories. Fix: ON DELETE SET NULL. The Source/DestinationCategoryId columns
--     are nullable and every read already tolerates a null category (renders "Unknown"), so the
--     transfer's financial audit row (amount, date, actor) survives an edit while a full budget
--     delete still removes the transfers via the Budget -> BudgetTransfers cascade.
--
-- Idempotent: guarded DROP/ADD, IF NOT EXISTS, and a de-dupe step that is a no-op once clean.
BEGIN;

-- (2) Relax the transfer -> category FKs first so nothing blocks the de-dupe deletes below.
ALTER TABLE public."BudgetTransfers"
    DROP CONSTRAINT IF EXISTS "FK_BudgetTransfers_BudgetCategories_SourceCategoryId";
ALTER TABLE public."BudgetTransfers"
    ADD CONSTRAINT "FK_BudgetTransfers_BudgetCategories_SourceCategoryId"
    FOREIGN KEY ("SourceCategoryId") REFERENCES public."BudgetCategories"("Id") ON DELETE SET NULL;

ALTER TABLE public."BudgetTransfers"
    DROP CONSTRAINT IF EXISTS "FK_BudgetTransfers_BudgetCategories_DestinationCategoryId";
ALTER TABLE public."BudgetTransfers"
    ADD CONSTRAINT "FK_BudgetTransfers_BudgetCategories_DestinationCategoryId"
    FOREIGN KEY ("DestinationCategoryId") REFERENCES public."BudgetCategories"("Id") ON DELETE SET NULL;

-- (1a) De-dupe existing rows BEFORE tightening the index so CREATE UNIQUE cannot fail on real data.
-- Keep the lowest Id per (BudgetId, COALESCE(CategoryId,0), COALESCE(AssignedUserId,0)); repoint
-- every referencing row from a losing duplicate to the survivor, then delete the losers.
CREATE TEMP TABLE _bc_dup_map ON COMMIT DROP AS
SELECT "Id" AS dup_id,
       MIN("Id") OVER (
           PARTITION BY "BudgetId", COALESCE("CategoryId", 0), COALESCE("AssignedUserId", 0)
       ) AS keep_id
FROM public."BudgetCategories";

DELETE FROM _bc_dup_map WHERE dup_id = keep_id;

-- Repoint the three tables that reference BudgetCategories("Id").
UPDATE public."BudgetPeriodCategorySnapshots" s
SET "BudgetCategoryId" = m.keep_id
FROM _bc_dup_map m
WHERE s."BudgetCategoryId" = m.dup_id;

UPDATE public."BudgetTransfers" t
SET "SourceCategoryId" = m.keep_id
FROM _bc_dup_map m
WHERE t."SourceCategoryId" = m.dup_id;

UPDATE public."BudgetTransfers" t
SET "DestinationCategoryId" = m.keep_id
FROM _bc_dup_map m
WHERE t."DestinationCategoryId" = m.dup_id;

DELETE FROM public."BudgetCategories" bc
USING _bc_dup_map m
WHERE bc."Id" = m.dup_id;

-- (1b) Replace the index with a NULL-collapsing unique index so duplicates cannot reappear.
DROP INDEX IF EXISTS public."IX_BudgetCategories_BudgetId_CategoryId_AssignedUserId";

CREATE UNIQUE INDEX IF NOT EXISTS "IX_BudgetCategories_BudgetId_CategoryId_AssignedUserId"
    ON public."BudgetCategories" USING btree
    ("BudgetId", (COALESCE("CategoryId", 0)), (COALESCE("AssignedUserId", 0)));

COMMIT;
