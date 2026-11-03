-- Hotfix: columns added by 0032/0033 were created as bare `timestamp`, but this
-- database's EF convention maps DateTime to `timestamp with time zone`. Reading
-- a bare-timestamp column yields Kind=Unspecified, and the NEXT save of that
-- entity throws (Npgsql: "Cannot write DateTime with Kind=Unspecified ...").
-- Broke User-row saves for anyone with an AI-consent stamp (v1.5.19) and the
-- nightly Wrapped stats update (since 0032). Values were written as UTC
-- instants, so AT TIME ZONE 'UTC' reinterprets them losslessly.
-- Conditional so a manually pre-patched database is not converted twice.

DO $$
BEGIN
  IF (SELECT data_type FROM information_schema.columns
      WHERE table_name = 'Users' AND column_name = 'AiConsentGrantedAt') = 'timestamp without time zone' THEN
    ALTER TABLE "Users" ALTER COLUMN "AiConsentGrantedAt"
      TYPE timestamp with time zone USING "AiConsentGrantedAt" AT TIME ZONE 'UTC';
  END IF;
  IF (SELECT data_type FROM information_schema.columns
      WHERE table_name = 'Users' AND column_name = 'AiConsentWithdrawnAt') = 'timestamp without time zone' THEN
    ALTER TABLE "Users" ALTER COLUMN "AiConsentWithdrawnAt"
      TYPE timestamp with time zone USING "AiConsentWithdrawnAt" AT TIME ZONE 'UTC';
  END IF;
  IF (SELECT data_type FROM information_schema.columns
      WHERE table_name = 'WrappedStatCutoffs' AND column_name = 'ComputedAt') = 'timestamp without time zone' THEN
    ALTER TABLE "WrappedStatCutoffs" ALTER COLUMN "ComputedAt"
      TYPE timestamp with time zone USING "ComputedAt" AT TIME ZONE 'UTC';
  END IF;
  IF (SELECT data_type FROM information_schema.columns
      WHERE table_name = 'WrappedBonusCardCaches' AND column_name = 'GeneratedAt') = 'timestamp without time zone' THEN
    ALTER TABLE "WrappedBonusCardCaches" ALTER COLUMN "GeneratedAt"
      TYPE timestamp with time zone USING "GeneratedAt" AT TIME ZONE 'UTC';
  END IF;
END $$;
