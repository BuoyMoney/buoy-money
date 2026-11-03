-- Manual rollback only: restores the original NO ACTION foreign keys.
BEGIN;
ALTER TABLE "Accounts" DROP CONSTRAINT IF EXISTS "FK_Accounts_Families_FamilyId";
ALTER TABLE "Accounts" ADD CONSTRAINT "FK_Accounts_Families_FamilyId" FOREIGN KEY ("FamilyId") REFERENCES "Families"("Id");
ALTER TABLE "PlaidConnections" DROP CONSTRAINT IF EXISTS "FK_PlaidConnections_Families_FamilyId";
ALTER TABLE "PlaidConnections" ADD CONSTRAINT "FK_PlaidConnections_Families_FamilyId" FOREIGN KEY ("FamilyId") REFERENCES "Families"("Id");
ALTER TABLE "Subscriptions" DROP CONSTRAINT IF EXISTS "FK_Subscriptions_Families_FamilyId";
ALTER TABLE "Subscriptions" ADD CONSTRAINT "FK_Subscriptions_Families_FamilyId" FOREIGN KEY ("FamilyId") REFERENCES "Families"("Id");
ALTER TABLE "AIInsights" DROP CONSTRAINT IF EXISTS "FK_AIInsights_Families_FamilyId";
ALTER TABLE "AIInsights" ADD CONSTRAINT "FK_AIInsights_Families_FamilyId" FOREIGN KEY ("FamilyId") REFERENCES "Families"("Id");
COMMIT;
