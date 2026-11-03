-- Founding-member reservations for the Buoy Money demand test: a $1 Stripe Checkout payment
-- that comes off the first year and is refundable any time. One row per paid Checkout session.
-- Additive only (new table), safe while the old version still serves the same database.
BEGIN;

CREATE TABLE IF NOT EXISTS public."Reservations" (
    "Id" serial PRIMARY KEY,
    "Email" varchar(320) NULL,
    "StripeCheckoutSessionId" varchar(255) NOT NULL,
    "StripeCustomerId" varchar(255) NULL,
    "StripePaymentIntentId" varchar(255) NULL,
    "AmountCents" integer NOT NULL,
    "Currency" varchar(3) NOT NULL DEFAULT 'usd',
    "PlanChoice" varchar(30) NOT NULL DEFAULT 'unsure',
    "Status" varchar(20) NOT NULL DEFAULT 'paid',
    "Source" varchar(100) NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "RefundedAt" timestamptz NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_Reservations_StripeCheckoutSessionId"
    ON public."Reservations" ("StripeCheckoutSessionId");
CREATE INDEX IF NOT EXISTS "IX_Reservations_StripePaymentIntentId"
    ON public."Reservations" ("StripePaymentIntentId");

COMMIT;
