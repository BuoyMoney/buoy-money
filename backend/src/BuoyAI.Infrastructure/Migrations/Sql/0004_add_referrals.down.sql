BEGIN;
DROP TABLE IF EXISTS public."Referrals";
DROP INDEX IF EXISTS public."IX_Users_ReferralCode";
ALTER TABLE public."Users" DROP COLUMN IF EXISTS "ReferralCode";
COMMIT;
