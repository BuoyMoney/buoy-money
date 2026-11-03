-- Bank connection alerts (task 11): when the "needs attention" email last went out for a
-- connection, so one outage sends one email. Cleared when the connection syncs again.
ALTER TABLE public."PlaidConnections"
    ADD COLUMN IF NOT EXISTS "AlertSentAt" timestamp with time zone NULL;
