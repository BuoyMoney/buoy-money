-- Reverse of 0020_add_oauth_connect_tables.up.sql (RFC #782 Phase 1).
-- Manual, reviewed rollback only — DbUp never auto-runs down scripts. Dropping these tables
-- invalidates every issued BuoyAI Connect grant (in-flight authorize requests, codes, and
-- refresh tokens), forcing every connected agent to re-authorize.
BEGIN;

DROP TABLE IF EXISTS public.oauth_refresh_tokens;
DROP TABLE IF EXISTS public.oauth_authorization_requests;
DROP TABLE IF EXISTS public.oauth_clients;

COMMIT;
