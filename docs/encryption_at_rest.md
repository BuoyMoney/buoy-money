# Encryption at rest

What is encrypted: bank connection secrets (`PlaidConnections.AccessToken`: a Plaid access token, or a SimpleFIN access URL), the Apple refresh token (`Users.AppleRefreshToken`), the authenticator (TOTP) secret (`Users.TotpSecret`) and the MFA recovery codes (`Users.RecoveryCodes`), through `IEncryptionService`. Code: `backend/src/BuoyAI.Infrastructure/Services/EncryptionService.cs`.

## Formats
- Legacy: base64(iv + AES-CBC), key = `Encryption:Key` padded or cut to 32 characters. No tamper check.
- v2: `v2:` + base64(nonce + tag + ciphertext), AES-256-GCM, key derived from `Encryption:Key` with HKDF-SHA256. Tampering fails to decrypt.

Decrypt always reads both. Which one Encrypt writes is the `Encryption:WriteV2` setting.

## Two-step rollout
1. This release: `Encryption:WriteV2` unset or false. Encrypt still writes legacy. The re-encrypt job does not run. Old copies during a blue-green deploy, and rollbacks, can read everything.
2. A later release, once no running or rollback target version predates v2 reading: set `Encryption:WriteV2=true`. New writes are v2 and `EncryptionReEncryptionService` converts old rows in batches (once per start, safe to repeat or interrupt). Turn off just the job with `Encryption:ReEncryptEnabled=false`.

After step 2 has converted rows, do not redeploy a version that cannot read v2.

## Authenticator secrets and recovery codes
`Users.TotpSecret` and `Users.RecoveryCodes` were stored as plain text before this. They have no legacy ciphertext, so a stored value is encrypted exactly when it starts with `v2:`; anything else is read as plain text (base32 and lowercase hex never start with `v2:`). Code: `MfaSecretProtector`.

They follow the same switch: with `Encryption:WriteV2` false, new values are still written as plain text (an old copy during a blue-green overlap or a rollback reads these columns directly). With it true, new values are `v2:` and the re-encrypt job converts existing rows. A value that fails to decrypt (tampered, wrong key) counts as a failed code check, never as a pass. Recovery codes are stored as one encrypted string and re-encrypted when a code is used.

## Key
`Encryption:Key` (env `Encryption__Key`) must be a real random secret, at least 32 characters (16 to 31 starts with a warning). Placeholders are refused at startup. Generate one with `openssl rand -base64 48`. Changing the key makes existing values unreadable; there is no rotation tool yet.
