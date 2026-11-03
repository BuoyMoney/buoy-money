-- Receipt scans: a user shares a screenshot of an order page, a photo of a paper receipt or a PDF;
-- BuoyAI reads the items and offers to split the matching bank charge by category.
--
-- One row per receipt sent in. The image is never stored, only the parsed items and the offered split.
BEGIN;

CREATE TABLE IF NOT EXISTS public."ReceiptScans" (
    "Id" serial PRIMARY KEY,
    "UserId" integer NOT NULL REFERENCES public."Users"("Id") ON DELETE CASCADE,
    "Status" varchar(30) NOT NULL DEFAULT 'unmatched',
    "Retailer" varchar(60) NULL,
    "OrderNumber" varchar(80) NULL,
    "OrderDate" timestamptz NULL,
    "Total" numeric(18,2) NULL,
    "ParsedJson" jsonb NULL,
    "ProposalJson" jsonb NULL,
    "TransactionId" integer NULL REFERENCES public."Transactions"("Id") ON DELETE SET NULL,
    "HintTransactionId" integer NULL,
    "Error" varchar(500) NULL,
    "ReceivedAt" timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "MatchedAt" timestamptz NULL,
    "AppliedAt" timestamptz NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "UpdatedAt" timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX IF NOT EXISTS "IX_ReceiptScans_UserId_Status" ON public."ReceiptScans" ("UserId", "Status");
CREATE INDEX IF NOT EXISTS "IX_ReceiptScans_TransactionId" ON public."ReceiptScans" ("TransactionId") WHERE "TransactionId" IS NOT NULL;

COMMIT;
