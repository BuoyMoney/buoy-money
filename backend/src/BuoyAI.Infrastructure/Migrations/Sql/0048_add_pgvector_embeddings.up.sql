-- Vector store in Postgres (Buoy Money task 08). One table holds every embedding collection
-- (chat history, transaction categories, help articles), so self-hosters run one database and
-- no Qdrant container. Each row records the model and dimension that produced it, so a model
-- change leaves old rows ignorable instead of corrupting search.
--
-- The pgvector extension is not installed everywhere. Hosted prod runs native Postgres 16
-- without it until the owner installs postgresql-16-pgvector. When the extension is missing or
-- cannot be created, this script does nothing (a NOTICE is raised) and the app, which still
-- uses Qdrant there, is unaffected. The app checks for the table at startup and logs a warning
-- if VectorStore:Provider=pgvector is set without it.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'vector') THEN
        RAISE NOTICE 'pgvector is not installed on this server; skipping embeddings table (see 0048_add_pgvector_embeddings)';
        RETURN;
    END IF;

    BEGIN
        CREATE EXTENSION IF NOT EXISTS vector;
    EXCEPTION WHEN insufficient_privilege THEN
        RAISE NOTICE 'not allowed to CREATE EXTENSION vector; skipping embeddings table';
        RETURN;
    END;

    CREATE TABLE IF NOT EXISTS public.embeddings (
        id          bigserial PRIMARY KEY,
        collection  text        NOT NULL,
        key         text        NOT NULL,
        user_id     integer     NULL,
        model       text        NOT NULL,
        dimensions  integer     NOT NULL,
        embedding   vector      NOT NULL,
        payload     jsonb       NOT NULL DEFAULT '{}'::jsonb,
        created_at  timestamptz NOT NULL DEFAULT now(),
        updated_at  timestamptz NOT NULL DEFAULT now(),
        CONSTRAINT ck_embeddings_dimensions CHECK (vector_dims(embedding) = dimensions)
    );

    CREATE UNIQUE INDEX IF NOT EXISTS ux_embeddings_collection_key
        ON public.embeddings (collection, key);
    CREATE INDEX IF NOT EXISTS ix_embeddings_collection_user
        ON public.embeddings (collection, user_id);
END
$$;
