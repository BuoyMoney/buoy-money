-- Move four email categories from opt-in to opt-out, and bring existing rows with them.
--
-- Changing the entity default only affects rows created afterwards, so without this every
-- existing user stays opted out of everything forever and the change reaches nobody.
--
-- SAFETY: this only updates rows that are still sitting on the ORIGINAL defaults for all
-- five marketing categories. Verified against production before writing: all six preference
-- rows were byte-identical to the shipped defaults, i.e. nobody has ever opened the
-- preferences page and chosen anything. The WHERE clause encodes that check rather than
-- trusting it, so if someone HAS made a choice — including deliberately opting out of one of
-- these four — their row is left exactly as they set it.
--
-- UpdatedAt is deliberately NOT bumped: this is us changing a default, not the user changing
-- a preference, and that column is how support tells those apart.
--
-- PromotionalOffers stays FALSE. It is the only unambiguously advertising category, and
-- GDPR/PECR require prior consent for direct marketing to EU/UK recipients.
BEGIN;

UPDATE public.user_email_preferences
SET
    "MonthlySummary"       = true,
    "FeatureAnnouncements" = true,
    "TipsAndBestPractices" = true,
    "ProductUpdates"       = true
WHERE
    "MonthlySummary"       = false
    AND "FeatureAnnouncements" = false
    AND "TipsAndBestPractices" = false
    AND "ProductUpdates"       = false
    AND "PromotionalOffers"    = false;

COMMIT;
