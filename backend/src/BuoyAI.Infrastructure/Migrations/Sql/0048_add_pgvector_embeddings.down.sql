-- Manual rollback only. Leaves the vector extension installed. Embeddings can be rebuilt
-- (chat history and transaction fingerprints re-embed; help articles re-index on startup).
DROP TABLE IF EXISTS public.embeddings;
DELETE FROM schemaversions WHERE scriptname LIKE '%0048_add_pgvector_embeddings.up.sql';
