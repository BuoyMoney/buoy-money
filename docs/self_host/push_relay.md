# Push relay contract

The wire contract for notifications to a phone from a self-hosted server. Three parties:

- **Phone** (the Buoy Money app): holds a private key that never leaves it.
- **Your server** (self-hosted): knows the phone's public key and a pairing token, encrypts each message.
- **Relay** (the BuoyAI push relay, built in a later task): holds the Firebase and Apple credentials, forwards
  opaque bytes to the phone. It cannot read a message.

Server code: `PushRelaySender`, `PushEnvelope` (backend). Phone code: `RelayPush*` (Flutter). The relay is
`cloud/BuoyAI.Cloud.PushRelay`; the server is tested against a fake one (`PushRelayTests`) and the relay against a fake
Firebase (its own tests).

## Pairing (phone to relay)

The phone gets its push token from Firebase (Android) or APNs through Firebase (iOS), then:

```
POST {relay}/v1/pair
{ "platform": "android" | "ios", "pushToken": "<FCM registration token>" }

200 { "pairingToken": "<opaque string, 32 to 256 characters>" }
```

The pairing token stands in for the push token. It is useless without the relay and it can be
revoked by the relay. The phone repeats this when its push token rotates and sends the same
`pairingToken` back so the relay updates instead of creating a second pairing:

```
{ "platform": "...", "pushToken": "...", "pairingToken": "<existing one, optional>" }
```

Unpair: `DELETE {relay}/v1/pair/{pairingToken}` answers 204.

The phone learns `{relay}` from its own server: `GET /api/system/version` returns
`pushRelayUrl` (null when the server has none).

## Registering with your server (phone to your server)

```
POST /api/notifications/devices        (signed in)
{ "platform": "relay", "token": "<pairingToken>", "publicKey": "<base64 of the phone's P-256 public key>" }
204
```

`publicKey` is the 65-byte uncompressed point (`0x04 | X | Y`), standard base64. The server stores
both; 400 if the key is not a valid P-256 point. The phone re-sends this on every app start and
resume, like an FCM token.

## Sending (your server to relay)

```
POST {relay}/v1/push
{ "pairingToken": "<token>", "envelope": "<base64url>", "ttl": 86400, "priority": "high" }

202   accepted for delivery
404 or 410   the pairing is unknown or revoked: the server deletes the device
429   rate limited (Retry-After header); the server counts it as a failed send
5xx   relay trouble; counted as failed, the device is kept
```

The request carries `Authorization: Bearer <per-server credential>` (the credential from pairing the server with a
BuoyAI Cloud account, see [bridge.md](bridge.md); or `PushRelay__ServerKey`). The relay answers `401` without a valid,
unrevoked credential. A server with no credential sends none, as before.

One request per device. `ttl` is seconds the relay may hold the message (the server sends 86400).
`priority` is `normal` or `high` (the server sends `high`).

The relay turns this into one push to the phone with a **fixed, generic visible text** (title
"Buoy Money", body "You have a new notification") and a data field `e` holding `envelope`
unchanged. It must not add, log or parse anything else. Android channel `buoyai_alerts`.

## Envelope (server to phone, opaque to the relay)

base64url without padding of:

```
version (1 byte, 0x01) | ephemeral public key (65 bytes) | nonce (12) | ciphertext | tag (16)
```

1. The server makes a fresh P-256 key pair for each message.
2. Shared secret = ECDH(ephemeral private key, phone public key), the 32-byte x coordinate.
3. Key = HKDF-SHA256(secret, salt = the 65-byte ephemeral public key, info = ASCII
   `buoymoney-push-v1`, 32 bytes).
4. AES-256-GCM encrypt with a random 12-byte nonce, associated data = the single version byte,
   16-byte tag.
5. Plaintext is UTF-8 JSON: `{"title": "...", "body": "...", "data": {"type": "...", "route": "..."}, "ts": 1760000000}`
   (`ts` is Unix seconds). Titles and bodies over 200 characters are cut so the whole message stays
   under the 4 KB push limit; the full text is in the app's notification list.

The phone decrypts with its private key and shows the real title and body, or routes a tap to
`data.route`. A message that fails to decrypt is dropped silently (wrong phone, tampering, or a
future version).

## What the relay learns

Which pairing token got a message and when, and its size. Not the text, the amounts, the account or
the server's address beyond the request source. It must keep no message bodies after delivery.

## Test vector

Both sides ship a test that decrypts this envelope, so the two implementations cannot drift:

```
phone private key d (base64): jO/CBsTnlvkQFg+2FrflxefFmt1StsH/fAWhZ6GN9eM=
phone public key (base64):    BBgp8Gnca6ohEbXjO3t/giLetNqBTPLeIVhfznBgUdFvhJ1LQJZc/dl/btGG1UEY4W96ze1IfbeXOPeDE3SiVNo=
envelope: AQRCEM4olUC53EDO-9e3wrTw4sMuuCZMeM0j-p0RXw41eh_MuRX_NBH8zIOEVeoOjH4uuTjJD3vLDKGDslInyha6HEE-rV_OcdMj0OmAyfJiVQUs2pOWZXa0SM_XSgmFhC5kmQTX6kcJIPe1E_3vRKj32wQOECl5f4XOkYFlUZYDMVV4fq9gFzmfeQgRFt0gzomQbCzuU5QYyz9RZCjJvP3CDlj2-ioOUeLu-qZDOvjxrHHp5NZ5i38y7P3_M7FmJCbRj_uVfEf5wIvozHeryo18FV73GvCZdKoMiGPrAA
plaintext: {"title":"Dining is over budget","body":"You spent 412 of 400","data":{"type":"budget_alert","route":"/budgets"},"ts":1760000000}
```
