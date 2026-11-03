-- Savings goal draws: a purchase earmarked "paid from goal X" on one account is later settled by
-- a real transfer out of the goal's linked account. This link ties the purchase to that transfer
-- so the goal can tell "in flight" money (earmarked, not yet moved) from drift, and so a matching
-- transfer can be recognised automatically at sync time.
ALTER TABLE "Transactions" ADD COLUMN IF NOT EXISTS "CoveringTransferTransactionId" integer NULL;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'FK_Transactions_CoveringTransfer'
  ) THEN
    ALTER TABLE "Transactions"
      ADD CONSTRAINT "FK_Transactions_CoveringTransfer"
      FOREIGN KEY ("CoveringTransferTransactionId") REFERENCES "Transactions"("Id") ON DELETE SET NULL;
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS "IX_Transactions_CoveringTransferTransactionId"
  ON "Transactions" ("CoveringTransferTransactionId")
  WHERE "CoveringTransferTransactionId" IS NOT NULL;
