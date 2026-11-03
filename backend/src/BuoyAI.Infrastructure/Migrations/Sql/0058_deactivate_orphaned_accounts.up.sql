-- Removing a bank used to leave its accounts active, so net worth and totals kept counting a frozen
-- balance while the Accounts page (which lists by active connection) showed nothing. The removal now
-- deactivates the accounts; this does the same for connections removed before that fix. Rows and
-- transactions stay. A bank linked again reactivates its accounts on the next sync. Idempotent.
UPDATE public."Accounts" a
SET "IsActive" = false,
    "UpdatedAt" = now()
FROM public."PlaidConnections" c
WHERE a."PlaidConnectionId" = c."Id"
  AND c."IsActive" = false
  AND a."IsActive" = true;
