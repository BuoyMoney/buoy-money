-- Reverse of 0013: drop the partial unique index on live subscriptions.
DROP INDEX IF EXISTS public."IX_UserSubscriptions_UserId_Live";
