-- Product entitlements (Buoy Money Cloud). One row per product a user holds through one billing
-- subscription: bank_sync, ai, instant_refresh, plus extra_logins whose Quantity is the count.
-- A projection of what the provider (Stripe) says, rebuilt on every subscription change, so the
-- app answers "may this user use X" from its own database and not from a token claim.
-- Additive only (new table), safe while the old version still serves the same database.
CREATE TABLE IF NOT EXISTS public."UserEntitlements" (
    "Id" serial PRIMARY KEY,
    "UserId" integer NOT NULL REFERENCES public."Users"("Id") ON DELETE CASCADE,
    "Entitlement" varchar(50) NOT NULL,
    "Quantity" integer NOT NULL DEFAULT 1,
    "Provider" smallint NOT NULL DEFAULT 0,
    "ExternalId" varchar(255) NOT NULL,
    "Status" varchar(20) NOT NULL DEFAULT 'active',
    "PaidThrough" timestamptz NULL,
    "EndsAt" timestamptz NULL,
    "Founding" boolean NOT NULL DEFAULT false,
    "CreatedAt" timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "UpdatedAt" timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_UserEntitlements_UserId_Entitlement_Provider_ExternalId"
    ON public."UserEntitlements" ("UserId", "Entitlement", "Provider", "ExternalId");
CREATE INDEX IF NOT EXISTS "IX_UserEntitlements_Provider_ExternalId"
    ON public."UserEntitlements" ("Provider", "ExternalId");
