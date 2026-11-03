ALTER TABLE "Users" DROP COLUMN IF EXISTS "SignupCountry";
ALTER TABLE "Users" DROP COLUMN IF EXISTS "PreferredLanguage";
ALTER TABLE user_push_preferences DROP COLUMN IF EXISTS time_zone_id;
