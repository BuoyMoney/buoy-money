-- AI consent (App Store guideline 5.1.2(i)): explicit permission before personal data is
-- shared with a third-party AI (Google Gemini). Three states: GrantedAt set = allowed;
-- WithdrawnAt set = blocked everywhere; both null = undecided (blocked on iOS only,
-- implicitly granted on other platforms at first AI use).

ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "AiConsentGrantedAt" timestamp NULL;
ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "AiConsentWithdrawnAt" timestamp NULL;
