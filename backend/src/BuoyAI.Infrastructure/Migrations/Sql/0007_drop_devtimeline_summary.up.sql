-- Remove the DevTimeline feature: the admin dev-timeline page, its controller, and the
-- GitHub-PAT integration were deleted. The DevTimelineSummaries cache table has no
-- readers or writers left. Safe to drop.
BEGIN;
DROP TABLE IF EXISTS public."DevTimelineSummaries";
COMMIT;
