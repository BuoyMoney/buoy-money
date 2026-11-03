-- Personal access tokens for BuoyAI Connect on a self-hosted server: a revocable, long-lived Connect token for
-- tools that cannot run the OAuth flow (Open WebUI, scripts). The id is the token's jti; the token is never stored.
CREATE TABLE IF NOT EXISTS public.connect_personal_tokens (
    id uuid NOT NULL,
    user_id integer NOT NULL,
    name text NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    expires_at timestamp with time zone NOT NULL,
    last_used_at timestamp with time zone NULL,
    revoked_at timestamp with time zone NULL,
    CONSTRAINT pk_connect_personal_tokens PRIMARY KEY (id),
    CONSTRAINT fk_connect_personal_tokens_user FOREIGN KEY (user_id) REFERENCES public."Users"("Id") ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_connect_personal_tokens_user_id ON public.connect_personal_tokens (user_id);
