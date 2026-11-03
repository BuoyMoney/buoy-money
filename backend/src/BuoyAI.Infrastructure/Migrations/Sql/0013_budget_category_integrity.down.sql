-- Reverse of 0013_budget_category_integrity.up.sql.
-- NOTE: de-duped/merged BudgetCategory rows are NOT restored (data loss is not reversible);
-- this only restores the schema (plain unique index + RESTRICT transfer FKs).
BEGIN;

-- Restore the plain (NULL-distinct) unique index.
DROP INDEX IF EXISTS public."IX_BudgetCategories_BudgetId_CategoryId_AssignedUserId";

CREATE UNIQUE INDEX IF NOT EXISTS "IX_BudgetCategories_BudgetId_CategoryId_AssignedUserId"
    ON public."BudgetCategories" USING btree ("BudgetId", "CategoryId", "AssignedUserId");

-- Restore ON DELETE RESTRICT on the transfer -> category FKs.
ALTER TABLE public."BudgetTransfers"
    DROP CONSTRAINT IF EXISTS "FK_BudgetTransfers_BudgetCategories_SourceCategoryId";
ALTER TABLE public."BudgetTransfers"
    ADD CONSTRAINT "FK_BudgetTransfers_BudgetCategories_SourceCategoryId"
    FOREIGN KEY ("SourceCategoryId") REFERENCES public."BudgetCategories"("Id") ON DELETE RESTRICT;

ALTER TABLE public."BudgetTransfers"
    DROP CONSTRAINT IF EXISTS "FK_BudgetTransfers_BudgetCategories_DestinationCategoryId";
ALTER TABLE public."BudgetTransfers"
    ADD CONSTRAINT "FK_BudgetTransfers_BudgetCategories_DestinationCategoryId"
    FOREIGN KEY ("DestinationCategoryId") REFERENCES public."BudgetCategories"("Id") ON DELETE RESTRICT;

COMMIT;
