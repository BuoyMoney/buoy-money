-- BuoyAI Cloud pairing for a self-hosted server (Buoy Money task 14). Additive; hosted never writes it.
-- One row at most. The credential and the two private keys are encrypted by the application.
CREATE TABLE IF NOT EXISTS public.cloud_pairings (
    id                    integer     NOT NULL,
    cloud_url             text        NOT NULL,
    server_id             uuid        NOT NULL,
    name                  text        NOT NULL,
    credential_cipher     text        NOT NULL,
    encryption_key_cipher text        NOT NULL,
    signing_key_cipher    text        NOT NULL,
    paired_at             timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pk_cloud_pairings PRIMARY KEY (id),
    CONSTRAINT ck_cloud_pairings_single CHECK (id = 1)
);
