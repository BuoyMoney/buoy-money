# BuoyAI Cloud bank feed (the bridge) and pairing

The Cloud bank feed gives a self-hosted server real bank connections without the self-hoster ever holding a Plaid
secret or a bank token. BuoyAI Cloud keeps the Plaid login; the server receives encrypted batches of
transactions. This page is the server's side of it. The Cloud services live under `cloud/` in this repo (private at
the 2.0.0 launch) and are described in their own READMEs; the app and the self-host server only speak HTTP to them
and never reference those projects.

Status: built and tested against a fake Cloud and Plaid sandbox fixtures. Nothing here has run against live Plaid.

## Turning it on

The pairing (and with it the free push relay) needs only `CloudBridge__Url`. Offering the bank feed as a provider also needs
`CloudBridge__BankFeedEnabled=true`, because it is a paid Cloud plan and connecting a bank fails without one. Hosted sets
neither, and the pairing routes (`/api/CloudBridge/...`) answer 404 there. As the server admin:

1. Link the server with a short code, like signing in on a TV (RFC 8628 device authorization). In the app, Settings,
   BuoyAI Cloud, press "Link with BuoyAI Cloud". The card shows a code such as `BCDF-GHJK`, a QR code and an "Open
   cloud.buoymoney.com" button. Approve the code on any phone or computer; the card notices by itself. Nothing comes back
   to this server's address, so plain http servers (a LAN address, Umbrel, CasaOS) link too. Behind it:
   - `POST /api/CloudBridge/link/device/start` (admin, body `{ "name": "Home server" }`, name optional, 60 characters at
     most; 400 when `CloudBridge__Url` is not set, 409 when already linked) asks the Cloud for a session and answers
     `{ "userCode", "verificationUri", "verificationUriComplete", "expiresAt", "interval" }`. The Cloud's secret device
     code stays in this server's memory with the admin's id and never reaches the app. Starting again replaces the
     session; a restart drops it.
   - A background loop on the server polls the Cloud every `interval` seconds (5 more after each `slow_down`, and on
     through network blips) until the code expires (10 minutes). When the owner approves, the Cloud hands back a
     one-time pairing code that confirms the server, and the server pairs with it and the name.
   - `GET /api/CloudBridge/link/device/status` answers `{ "state": "none|waiting|linked|expired|denied|failed",
     "userCode", "expiresAt", "verificationUri", "verificationUriComplete", "message" }` (the code fields while there is
     a session, `message` only for `failed`). The app asks every 3 seconds while the card is on screen and the app is
     in front, and shows the code again if the screen is reopened. `DELETE /api/CloudBridge/link/device` stops waiting
     (the code just runs out on the Cloud); unlinking clears it too.
   - On the Cloud page the owner signs in (or makes an account), enters the code (the QR and the button fill it in),
     sees the server's name, version and when it asked, and presses Approve with their second factor, or Deny. Only
     approve a code shown on your own server: whoever approves a code puts that server on their Cloud account.
   - The Cloud side is the `/v1/pairing/link-sessions` calls the bridge mounts (start, poll, owner look-up, approve,
     deny); their shapes, limits and storage are in `cloud/BuoyAI.Cloud.Pairing/README.md`. Wrong codes count against
     the owner, so codes cannot be guessed.
   - The older browser round trip still works as a fallback: `POST /api/CloudBridge/link/start` answers
     `{ "url": "https://buoy-ai.com/app/cloud/link?state=..&name=..&callback=.." }` (a one-time state kept as a SHA-256
     in memory for 15 minutes), the owner approves there, and buoy-ai.com sends the browser to the anonymous
     `GET /api/CloudBridge/link/callback?code=..&state=..`, which pairs and lands on `/app/settings/cloud?linked=1` or
     `?link_error=cancelled|expired|failed`. It needs `BUOY_ORIGIN` on https (or plain http on `localhost`), else it
     answers 400 `link_needs_https`. The app no longer starts it.
2. Or pair by hand with a code. On buoy-ai.com, Settings, BuoyAI Cloud, Your servers, press "Make a pairing code" (second
   factor and a Cloud product needed, as above), then on the server "Enter a pairing code", or
   `POST /api/CloudBridge/pair` with `{ "code": "ABCDE-FGHJK", "name": "Home server" }`
   (`GET /api/CloudBridge/status` shows the state, `DELETE /api/CloudBridge/pair` forgets it on this server).
3. A server paired with a typed code waits for the owner: confirm it in the same list on buoy-ai.com ("Waiting for you
   to confirm", then Confirm, with the second factor again; the confirmation email is not built yet). Until then the
   Cloud sends no bank data to it. A server linked by code (or by the browser round trip) skips this, because the owner
   approved it with a second factor. Revoke in that list cuts a server off.

   Only approve a code shown on your own server. A code someone else shows you would put their server on your Cloud
   account. The approve page shows the server's name, version and when it asked; the code works for 10 minutes.
4. Connect a bank: `POST /api/aggregators/Bridge/connect/start` answers with a page address (`RedirectUrl`). The
   person opens it, reads where the data will go, ticks the box, and signs in at their bank. Back in the app,
   `POST /api/aggregators/Bridge/connect/complete` (body `{ "artifact": "<the page address or its session id>" }`)
   adopts the one bank that page made, for the user who started it. The Cloud keys banks by server, not by person, so
   this server remembers who started which page (in memory, for the life of the page, about half an hour) and a user
   can only adopt the bank their own page made. If the artifact names nothing this user started, their most recent
   page is used.
5. A bank that needs a new sign-in is fixed with `POST /api/aggregators/Bridge/connections/{id}/reconnect`: it answers
   with a Cloud page (Plaid update mode for the same bank, so no second login is made and the login cap is not used),
   and `connect/complete` after the person is done puts the connection back to active. The Flutter accounts screen
   does this from a connection's reconnect action.

In the app, the administrator pairs from Settings, BuoyAI Cloud ("Link with BuoyAI Cloud" shows a code to approve, or enter a code made on buoy-ai.com), and anyone connects a bank from Accounts, Add a bank, BuoyAI Cloud once the server is paired. Reconnect is a connection's action in the accounts screen.

| Setting | Meaning |
|---|---|
| `CloudBridge__Url` | Base address of the Cloud bridge (https; plain http only for localhost). Empty means off. Enough to pair for the push relay. |
| `CloudBridge__BankFeedEnabled` | Offer the Cloud bank feed as a provider (false). Needs `CloudBridge__Url` and a paid Cloud plan. |
| `CloudBridge__LinkUrl` | The buoy-ai.com page of the older browser round trip (`https://buoy-ai.com/app/cloud/link`). The code link does not use it: the Cloud names its own approve page. |
| `CloudBridge__ServerVersion` | Version reported to the Cloud. Empty means the build version. |
| `CloudBridge__MinSyncIntervalMinutes` | Least wait between sync attempts on one bank (30). The Cloud also caps pulls. |
| `CloudBridge__MaxBatchesPerSync` | Most batches pulled for one bank in one sync (40). |
| `PushRelay__ServerKey` | Optional. Overrides the pairing credential for push relay calls. |

## When the bank needs attention

The Cloud receives Plaid's webhooks and keeps the result on the bank, where this server reads it (the sync asks the Cloud
for the bank list each time, which makes no Plaid request). This server then does what hosted does for its own Plaid banks:

| What the Cloud reports | What this server does |
|---|---|
| `status` is `login_required` or `revoked` | the connection becomes "login required" and the user gets the in-app alert, push and one email per outage |
| `notice` is `pending_expiration` or `pending_disconnect` | the connection shows expiring or to be disconnected, with one alert (the email is reserved for a real outage); the bank keeps syncing |
| `notice` is `new_accounts_available` | one alert, no status change; reconnecting turns on the account picker |
| a flagged connection comes back `active` with no notice (the Cloud saw `LOGIN_REPAIRED`, or a reconnect finished) | the connection returns to active by itself and the "needs attention" notification becomes "connection restored" |

A connection waiting for a sign-in is not synced, so a short job in the sync loop asks the Cloud about those connections
once per sync interval and clears the ones that healed. Alerts for a heads-up are sent once per heads-up
(`PlaidConnections.BridgeNoticeAt` remembers the last one alerted, migration `0057`). Reconnecting is the existing
`POST /api/aggregators/Bridge/connections/{id}/reconnect`.

The Cloud bridge also needs its Plaid redirect URI set to its `/link/oauth-return` page for banks that use OAuth (see the
bridge README).

## What the pairing stores

Migration `0054_cloud_pairing` adds one table, `cloud_pairings` (at most one row). The server makes two P-256 key pairs
when it pairs: one that opens deliveries and one that signs requests. The private halves and the credential are
encrypted at rest with AES-256-GCM, keyed from `Encryption:Key` (so changing that key makes the pairing unreadable and
the server must pair again). The Cloud holds only the public keys and a hash of the credential.

## Wire contract (server to Cloud)

Every call carries `Authorization: Bearer bmk_...`, `X-Buoy-Server-Version`, `X-Buoy-Timestamp` (unix seconds) and
`X-Buoy-Signature`. The signature is ECDSA P-256 with SHA-256 (fixed size r then s, base64) over four lines joined by
`\n`: the method in capitals, the path and query, the lowercase hex SHA-256 of the body (of nothing for no body), and the
timestamp. The window is 5 minutes and a signature works once. Full detail: `cloud/BuoyAI.Cloud.Pairing/README.md`.

| Call | Answer |
|---|---|
| `GET /bridge/v1/items` | the banks held for this server: `id`, `institutionName`, `status` (`active`, `login_required`, `revoked`), `notice` (null, `pending_expiration`, `pending_disconnect` or `new_accounts_available`) with `noticeAt`, `updatesAvailable`, `overCap`, ... |
| `POST /bridge/v1/link/start` | `{ sessionId, url, expiresAt }`. With body `{ "itemId": "..." }` it starts a sign-in again for that bank (Plaid update mode) instead of a new bank. |
| `GET /bridge/v1/link/{sessionId}/result` | `{ status, itemId }`: `pending`, `finished` (with the bank it made), `failed` or `expired`. Only the server that started the session can ask (`404` otherwise). |
| `POST /bridge/v1/items/{id}/pull` | `204` nothing new, or a batch (below) |
| `POST /bridge/v1/batches/{batchId}/ack` | `204`; `409` if that batch was already acknowledged or replaced |
| `DELETE /bridge/v1/items/{id}` | `204`; removes the bank at Plaid, which ends its monthly charge |
| `GET /bridge/v1/billing` | `{ subscription, upcoming, lookupFailed }` for the Cloud account this server is paired to: the plan and the next invoice line by line. The account comes from the pairing, never from the request. `403` until the owner confirms the server; `404 billing_not_configured` where the Cloud has no billing. The server shows it at `GET /api/cloud/billing` (administrator only). |
| `POST /bridge/v1/billing/portal-session` | Body `{ "returnUrl": "https://..." }` (optional; a non-https or missing address falls back to the Cloud's own page). `{ url }`: a Stripe page for the account's card and invoices. |

Refusals carry `{ "error": "...", "message": "..." }`: `login_required` (409), `upgrade_required` (426, the signed
version manifest says this server is too old), `cap_reached` or `over_cap` (403), `awaiting_owner_confirmation` and
`cooling_off` (403), `rate_limited` and `daily_cap` (429 with Retry-After), `not_ready` (409, the bank is still loading its first transactions), `paused` (503, the kill switch), `no_subscription` (402).
`401` means the Cloud no longer accepts this server (revoked); the server reports the bank as needing attention.
A `429`, `daily_cap`, `not_ready` or `paused` answer is not a failure: the server does not count it toward the four
failures that move a bank to the error state, waits at least the `Retry-After` it was given, and a throttle part way
through a long catch-up stops cleanly after the pages already acknowledged.

### A batch

```
{ "batchId": "...", "itemId": "...", "cursorAfter": "...", "hasMore": false,
  "added": 12, "modified": 0, "removed": 1, "sealed": "<base64url>" }
```

`sealed` is HPKE (RFC 9180 base mode, DHKEM(P-256, HKDF-SHA256) 0x0010, HKDF-SHA256, AES-128-GCM) to the server's
encryption key: `enc (65 bytes) | ciphertext | tag (16)`, base64url without padding. `info` is the ASCII text
`buoymoney-feed-v1`. The associated data binds the batch to this server and this position:

```
{serverId as 32 hex}|{batchId as 32 hex}|{itemId as 32 hex}|{cursorAfter}
```

A batch replayed to another server, moved to another position, or edited fails to decrypt. The plaintext is JSON:
`batch_id`, `item_id`, `institution_name`, `cursor_before`, `cursor_after`, `has_more`, `added[]`, `modified[]`,
`removed[]` (Plaid's `/transactions/sync` objects, passed through untouched), `accounts[]`, `initial_update_complete`,
`historical_update_complete`, `created_at`, `bridge_version`. The Cloud does no mapping to this app's schema.

### Order of events for one sync

1. List the banks. Skip a bank with `updatesAvailable: false` (after its first sync). This call makes no Plaid request.
2. Pull one page. Open it. Store it. Only then acknowledge it. The Cloud moves its cursor on the acknowledgement, so a
   crash between storing and acknowledging gets the same page again, and the ingest dedupe skips what is already stored.
3. Repeat while `hasMore`, up to `CloudBridge__MaxBatchesPerSync`.

What is mapped: Plaid accounts become accounts (checking, savings, credit card; investment and loan accounts are
dropped by the same account-type rule as everywhere), amounts flip sign (Plaid shows money out as positive), ids get the
prefix `bm:`, a posted charge replaces its pending hold through `pending_transaction_id`, `removed` deletes rows the user
has not worked on. Category hints from Plaid go to the ingest pipeline; the rules and the AI queue decide.
Known limit: `modified` only matters for rows this server does not have yet (the ingest seam creates, it does not update).

### Test vector

Both implementations decrypt this exact envelope (`BridgeHpkeTests` here, `HpkeTests.Shared_vector_opens` in the Cloud
tests):

```
phone/server private key d (base64): jO/CBsTnlvkQFg+2FrflxefFmt1StsH/fAWhZ6GN9eM=
public key (base64):                 BBgp8Gnca6ohEbXjO3t/giLetNqBTPLeIVhfznBgUdFvhJ1LQJZc/dl/btGG1UEY4W96ze1IfbeXOPeDE3SiVNo=
serverId (32 hex):                   11111111222233334444555555555555
batchId (32 hex):                    aaaaaaaabbbbccccddddeeeeeeeeeeee
itemId (32 hex):                     99999999888877776666555555555555
cursorAfter:                         cursor-after-1
envelope: BLspCP7ZoDHrAuo1yUzjPPMGy5saJh_BKmPZIgWQmhZutuN7Ggb6g4zjerIxRx8U-PCYDknVNxo3rM_0zh8agRrfIrZNsb0snG9eVPMyZ41XbICAA-Ek2luasFT1asTT3jYdNZA1lwDQoLx2nWH_Q5DeT2mSQ-9sTFYGnEZB8RF6JyBK7Ki7eMupqFTiF4_kNOGncEIk5qhMQUsik7UAYwAbFGgLdS3_yfo
plaintext: {"batch_id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","added":[],"modified":[],"removed":[]}
```

The HPKE code follows the RFC's labeled extract and expand steps but has not been checked against the RFC's published
test vectors yet; the two implementations here share one author. A reviewer should run those vectors before launch.

## What you are trusting

- BuoyAI Cloud sees your bank logins' Plaid token and, in memory for a moment, each page it relays. It stores no
  transaction. It keeps the token, where each feed left off, a call ledger and an audit log without transaction content.
- After a batch arrives, BuoyAI cannot protect it. Your server holds your data.
- The server never sees a Plaid secret or bank token. A stolen server credential alone cannot call the bank API: calls
  are signed, and the owner can revoke the server in the Cloud portal at any time. Re-pairing a server needs a fresh
  second factor, needs the owner to confirm again, and pauses delivery for 24 hours.

## Push relay credential

The relay in `cloud/BuoyAI.Cloud.PushRelay` requires the same per-server credential on `POST /v1/push`. The server adds
`Authorization: Bearer <credential>` when it is paired or `PushRelay__ServerKey` is set. See [push_relay.md](push_relay.md).
