-- Deleting a family failed whenever the family had linked accounts, bank connections,
-- subscriptions or AI insights: these four foreign keys to "Families" were created without a
-- delete rule (NO ACTION), so Postgres refused the DELETE. Every other table that points at a
-- family either cascades (members, invitations, family categories) or sets NULL (budgets,
-- savings goals, users). These rows belong to the user who linked them, so on family delete
-- they stay and simply stop belonging to a family: ON DELETE SET NULL.
BEGIN;

ALTER TABLE "Accounts" DROP CONSTRAINT IF EXISTS "FK_Accounts_Families_FamilyId";
ALTER TABLE "Accounts" ADD CONSTRAINT "FK_Accounts_Families_FamilyId"
  FOREIGN KEY ("FamilyId") REFERENCES "Families"("Id") ON DELETE SET NULL;

ALTER TABLE "PlaidConnections" DROP CONSTRAINT IF EXISTS "FK_PlaidConnections_Families_FamilyId";
ALTER TABLE "PlaidConnections" ADD CONSTRAINT "FK_PlaidConnections_Families_FamilyId"
  FOREIGN KEY ("FamilyId") REFERENCES "Families"("Id") ON DELETE SET NULL;

ALTER TABLE "Subscriptions" DROP CONSTRAINT IF EXISTS "FK_Subscriptions_Families_FamilyId";
ALTER TABLE "Subscriptions" ADD CONSTRAINT "FK_Subscriptions_Families_FamilyId"
  FOREIGN KEY ("FamilyId") REFERENCES "Families"("Id") ON DELETE SET NULL;

ALTER TABLE "AIInsights" DROP CONSTRAINT IF EXISTS "FK_AIInsights_Families_FamilyId";
ALTER TABLE "AIInsights" ADD CONSTRAINT "FK_AIInsights_Families_FamilyId"
  FOREIGN KEY ("FamilyId") REFERENCES "Families"("Id") ON DELETE SET NULL;

COMMIT;
