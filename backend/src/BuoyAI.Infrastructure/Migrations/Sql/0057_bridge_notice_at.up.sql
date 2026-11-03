-- Cloud bank feed heads-up alerts (task 27): the time of the Cloud's last heads-up (expiring, to be disconnected,
-- new accounts) this server has already alerted about, so a notice the Cloud keeps showing alerts once.
ALTER TABLE public."PlaidConnections"
    ADD COLUMN IF NOT EXISTS "BridgeNoticeAt" timestamp with time zone NULL;
