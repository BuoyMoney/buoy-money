-- Manual, reviewed rollback for 0046. Never auto-run by DbUp.
--
-- Drops the reservation records. Refund any paid reservations in Stripe first, since this
-- table is the only local record of who reserved.
BEGIN;

DROP TABLE IF EXISTS public."Reservations";

COMMIT;
