-- Store Apple "Sign in with Apple" refresh token (ENCRYPTED at rest) so the grant
-- can be revoked at Apple when the user deletes their account — Apple's account-
-- deletion requirement. Captured at login by exchanging the authorization code.
-- See AppleAuthClient + AuthService.AppleLoginAsync / DeleteAccountAsync (#).
BEGIN;

ALTER TABLE public."Users"
    ADD COLUMN IF NOT EXISTS "AppleRefreshToken" text;

COMMIT;
