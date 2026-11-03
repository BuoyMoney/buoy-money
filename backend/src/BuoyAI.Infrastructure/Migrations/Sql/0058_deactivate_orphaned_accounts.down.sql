-- Manual rollback only (never auto-run).
-- Nothing to undo: the up script only flips stale accounts to inactive, and which ones it touched is
-- not recorded. Turning them back on would restore the bug (frozen balances in net worth). To bring
-- one back, link the bank again (the sync reactivates it) or set "IsActive" = true for that account.
SELECT 1;
