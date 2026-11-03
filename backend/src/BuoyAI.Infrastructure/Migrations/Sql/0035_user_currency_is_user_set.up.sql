-- #941 currency step 2: track whether the user explicitly chose their currency.
-- Registration defaults Currency to 'USD' without asking; the first linked bank
-- account may adopt its currency ONLY while this flag is false, and a profile
-- edit sets it true so a deliberate choice is never overridden by a later link.
ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "CurrencyIsUserSet" boolean NOT NULL DEFAULT false;
