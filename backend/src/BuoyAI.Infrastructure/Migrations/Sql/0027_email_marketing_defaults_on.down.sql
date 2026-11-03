-- Manual, reviewed rollback for 0027. Never auto-run by DbUp.
--
-- IMPERFECT BY NATURE, and worth understanding before running. The up-migration cannot be
-- distinguished after the fact from a user who genuinely turned all four categories on, so
-- this reverts BOTH populations. Anyone who opted in for real between the two runs loses that
-- choice and silently stops receiving mail they asked for.
--
-- Only the exact all-four-on-and-promos-off shape is touched, which limits the blast radius
-- to rows that look like the migration made them, but it cannot rule out a real opt-in that
-- happens to match.
--
-- Revert the entity defaults in UserEmailPreferences.cs at the same time, or newly created
-- users will keep arriving opted in.
BEGIN;

UPDATE public.user_email_preferences
SET
    "MonthlySummary"       = false,
    "FeatureAnnouncements" = false,
    "TipsAndBestPractices" = false,
    "ProductUpdates"       = false
WHERE
    "MonthlySummary"       = true
    AND "FeatureAnnouncements" = true
    AND "TipsAndBestPractices" = true
    AND "ProductUpdates"       = true
    AND "PromotionalOffers"    = false;

COMMIT;
