DROP INDEX IF EXISTS "IX_Transactions_CoveringTransferTransactionId";
ALTER TABLE "Transactions" DROP CONSTRAINT IF EXISTS "FK_Transactions_CoveringTransfer";
ALTER TABLE "Transactions" DROP COLUMN IF EXISTS "CoveringTransferTransactionId";
