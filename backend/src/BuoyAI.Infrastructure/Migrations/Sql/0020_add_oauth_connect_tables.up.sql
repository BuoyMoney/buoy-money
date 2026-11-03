-- BuoyAI Connect (RFC #782 Phase 1) — OAuth 2.1 authorization-server persistence.
-- Three tables backing the /api/oauth/* endpoints:
--   oauth_clients                 — pre-registered + CIMD-discovered clients (public clients, PKCE).
--   oauth_authorization_requests  — short-lived (10-min) authorize-request lifecycle + single-use
--                                   authorization codes (60s). One row per /authorize hit.
--   oauth_refresh_tokens          — rotating refresh tokens, stored SHA-256 hashed, SEPARATE from
--                                   the first-party refresh_tokens table (different audience/lifecycle).
-- snake_case to match 0019; DbUp owns the schema, the EF mappings just agree with it.
-- Idempotent: CREATE TABLE / INDEX IF NOT EXISTS are no-ops if they already exist.
BEGIN;

CREATE TABLE IF NOT EXISTS public.oauth_clients (
    client_id text NOT NULL,
    client_name text NOT NULL,
    client_uri text NULL,
    logo_uri text NULL,
    -- JSON array of exact-match redirect URIs.
    redirect_uris text NOT NULL,
    -- true when discovered via a Client ID Metadata Document (public client, refetched >24h old).
    is_cimd boolean NOT NULL DEFAULT false,
    cimd_fetched_at timestamp with time zone NULL,
    created_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT pk_oauth_clients PRIMARY KEY (client_id)
);

CREATE TABLE IF NOT EXISTS public.oauth_authorization_requests (
    id uuid NOT NULL,
    client_id text NOT NULL,
    -- Bound at consent time from the authenticated first-party principal; null until decided.
    user_id integer NULL,
    redirect_uri text NOT NULL,
    scope text NOT NULL,
    state text NULL,
    code_challenge text NOT NULL,
    code_challenge_method text NOT NULL,
    resource text NULL,
    -- Single-use authorization code (60s TTL); null until approved.
    code text NULL,
    code_expires_at timestamp with time zone NULL,
    consumed boolean NOT NULL DEFAULT false,
    -- null = undecided, true = approved, false = denied.
    approved boolean NULL,
    created_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    expires_at timestamp with time zone NOT NULL,
    CONSTRAINT pk_oauth_authorization_requests PRIMARY KEY (id)
);

-- Token exchange looks the request up by its authorization code.
CREATE INDEX IF NOT EXISTS ix_oauth_authorization_requests_code
    ON public.oauth_authorization_requests (code);

CREATE TABLE IF NOT EXISTS public.oauth_refresh_tokens (
    id uuid NOT NULL,
    user_id integer NOT NULL,
    client_id text NOT NULL,
    -- SHA-256 (hex) of the opaque 256-bit refresh token; the raw value is never persisted.
    token_hash text NOT NULL,
    scope text NOT NULL,
    resource text NULL,
    expires_at timestamp with time zone NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    revoked_at timestamp with time zone NULL,
    -- Rotation lineage: the token that replaced this one, if any. A reused (already-revoked)
    -- token means replay/theft → the whole rotation chain is revoked (see OAuthConnectService).
    replaced_by uuid NULL,
    CONSTRAINT pk_oauth_refresh_tokens PRIMARY KEY (id)
);

-- Rotation lookup is by token hash.
CREATE INDEX IF NOT EXISTS ix_oauth_refresh_tokens_token_hash
    ON public.oauth_refresh_tokens (token_hash);
-- Code-reuse revocation revokes all active tokens for a (user, client) grant.
CREATE INDEX IF NOT EXISTS ix_oauth_refresh_tokens_user_client
    ON public.oauth_refresh_tokens (user_id, client_id);

COMMIT;
