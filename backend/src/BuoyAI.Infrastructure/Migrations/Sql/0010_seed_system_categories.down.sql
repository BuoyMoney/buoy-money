-- 0010_seed_system_categories.down.sql
--
-- Reverse of 0010_seed_system_categories.up.sql. Manual, reviewed rollback only (NOT auto-run).
--
-- Removes the built-in system categories (ids 1-99). Only safe if no Transactions / Budgets /
-- rules still reference these ids (the FKs would block the delete otherwise) -- which is why this
-- is a deliberate, supervised operation and not something DbUp reverses automatically.

BEGIN;

DELETE FROM public."Categories"
WHERE "IsSystem" = true
  AND "Id" BETWEEN 1 AND 99;

COMMIT;
