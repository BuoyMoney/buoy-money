-- Records which AI-sharing disclosure a user agreed to (AiConsent.CurrentVersion in code).
--
-- App Review rejected the iOS build under 5.1.1(i)/5.1.2(i): data went to Google Gemini
-- before the app had clearly said what is sent and to whom, and asked. Consent is now
-- explicit on every platform and the sheet lists every data category.
--
-- Existing grants keep their timestamps but get NULL here, which the code treats as "not
-- current": some of them were implied on Android/web without the user seeing anything, and the
-- rest were given on a sheet that listed only part of what is sent. Everyone is asked once on
-- the new sheet; until then background AI (categorization, insights, embeddings) is skipped.
BEGIN;

ALTER TABLE public."Users"
    ADD COLUMN IF NOT EXISTS "AiConsentVersion" integer NULL;

COMMIT;
