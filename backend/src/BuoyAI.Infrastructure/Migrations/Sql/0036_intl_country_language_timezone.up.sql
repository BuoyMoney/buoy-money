-- #941 country plumbing: where a user signed up from (IP-geolocated, ISO-3166
-- alpha-2; null when geolocation was unavailable), their preferred UI language
-- (scaffold for the email/push locale layer; null = English), and the IANA
-- timezone id for push scheduling (fixes DST drift of the fixed minute offset).
ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "SignupCountry" varchar(2);
ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "PreferredLanguage" varchar(10);
ALTER TABLE user_push_preferences ADD COLUMN IF NOT EXISTS time_zone_id text;
