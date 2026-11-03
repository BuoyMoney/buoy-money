-- Self-host sign-in and push (Buoy Money task 09). All additive; hosted never reads these.
--
--   Users.IsInstanceAdmin        the first account on a self-hosted server is its admin
--   instance_settings            small key/value store the admin edits (registration_open)
--   registration_invites         one-time codes an admin hands out while registration is closed
--   user_device_tokens.public_key  phone public key for push relay devices (null for FCM tokens)
ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "IsInstanceAdmin" boolean NOT NULL DEFAULT false;

ALTER TABLE public.user_device_tokens ADD COLUMN IF NOT EXISTS public_key text NULL;

CREATE TABLE IF NOT EXISTS public.instance_settings (
    key         text        NOT NULL,
    value       text        NOT NULL,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pk_instance_settings PRIMARY KEY (key)
);

CREATE TABLE IF NOT EXISTS public.registration_invites (
    id                  serial      NOT NULL,
    code_hash           text        NOT NULL,
    note                text        NULL,
    created_by_user_id  integer     NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    expires_at          timestamptz NOT NULL,
    used_by_user_id     integer     NULL,
    used_at             timestamptz NULL,
    CONSTRAINT pk_registration_invites PRIMARY KEY (id)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_registration_invites_code_hash
    ON public.registration_invites (code_hash);
