-- Reverse of 0052. Run by hand (psql) and delete the matching schemaversions row.
-- buoy:reviewed-destructive: the table is a rebuildable projection of Stripe state (a refresh from Stripe repopulates it)
DROP TABLE IF EXISTS public."UserEntitlements";
