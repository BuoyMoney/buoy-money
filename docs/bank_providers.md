# Bank providers

BuoyAI reads bank data through `IBankAggregator`
(`backend/src/BuoyAI.Core/Interfaces/IBankAggregator.cs`). A provider is one class plus one line in
`DependencyInjection.cs`; the registry, the generic endpoints and the background sync find it by its
`BankProvider` value. Connections of every provider live in `PlaidConnections` (the name predates the
seam), tagged by `Provider`.

| Provider | Where it runs | Auth stored in `AccessToken` |
|---|---|---|
| Plaid (`0`) | Hosted only | Plaid access token, encrypted |
| SimpleFIN (`5`) | Self-host by default, hosted when switched on | SimpleFIN access URL, encrypted |
| Cloud bridge (`6`) | Only where `CloudBridge__Url` and `CloudBridge__BankFeedEnabled` are set (self-host, paid BuoyAI Cloud) | Nothing: the Cloud holds the Plaid token; see [self_host/bridge.md](self_host/bridge.md) |

## What a provider implements

Connect (`StartConnectionAsync`, `CompleteConnectionAsync`), sync (`SyncConnectionTransactionsAsync`),
the sync schedule (`MinimumSyncInterval`, `SyncJitter`), and the lifecycle (`DisconnectConnectionAsync`,
`TryRemoveConnectionAsync`, `HandleSyncFailureAsync`). Transactions go through
`ITransactionIngestService`, never straight into the table.

The background loop (`PlaidTransactionSyncService`, every 10 minutes) asks each connection's provider
for its schedule through `SyncThrottle`: a connection is skipped until `MinimumSyncInterval` plus its own
share of `SyncJitter` has passed since the last attempt. Failed attempts count. Plaid asks for no wait.

External ids must start with a provider prefix (SimpleFIN uses `sfin:`) because accounts and
transactions of all providers share one unique column each.

## Alerts

One service, `IBankConnectionAlertService`, for every provider. Whatever moves a connection into a
"needs the user" state calls it with the saved connection:

- an in-app notification with its push copy, every time;
- an email, once per outage (`PlaidConnections.AlertSentAt`; cleared by the next clean sync; it is not repeated
  while the outage lasts, and the send is claimed with one conditional update so concurrent alerts cannot double it). The email honours the user's "connection issues" email preference.

## SimpleFIN

SimpleFIN is a bridge the user pays directly (about $15 a year). BuoyAI pays nothing.

**Connect:** the user picks SimpleFIN in Accounts, Add a bank, opens the bridge (`SimpleFin:BridgeUrl`),
makes a setup token, pastes it in. The server claims it once, stores the returned access URL encrypted, and runs the first
sync. The token is the Base64 of a claim URL that the user supplied, so only `https` on an allowed
host is called (`SimpleFin:AllowedHosts`, default `simplefin.org` and its subdomains), only to public
IP addresses, with redirects off. Tokens and access URLs are never logged.

**Reconnect:** pasting a new token while exactly one active SimpleFIN connection is broken (login
required or errored) repairs that connection. A disconnected connection is never matched, so its
token adds a new connection. If two or more are broken the token cannot be matched to one and is
refused before it is spent; remove the ones no longer used first. A connection that is still Active
but flagged (the bridge wants a fresh sign-in at a bank) is fixed at the bridge, and a new token adds
another connection. Account and transaction ids are namespaced by the connection row, so two
connections that reuse the same bridge ids never overwrite each other.

**Switching it off:** `SimpleFin:Enabled=false` stops offering, connecting and syncing, but the
aggregator stays reachable for removal, so existing connections can still be disconnected and their
stored credential wiped.

**Sync:** at most every `MinSyncIntervalHours` (6) plus up to `JitterMinutes` (60) per connection, so
about four calls a day against the bridge's 24 a day. Each call asks for the loop's window widened by
four days (a five-day overlap with the previous one), in slices of at most 90 days. Only posted
transactions are imported: the bridge gives a pending charge a different id and no link to its posted
charge, so keeping pending ones would show every charge twice.

**Accounts:** the bridge sends no account type, so it is guessed from the name (credit card, savings,
else checking). Retirement, brokerage and loan names are outside the budget and are skipped. A card's
negative balance is stored as a positive amount owed, as Plaid's is. Transactions arrive uncategorized
and flagged for review; rules and the AI queue run as for any other source.

**Failures:** a 401/403/402 from the bridge means the access URL is dead: the connection goes to
`LoginRequired` and the user is alerted. A sign-in problem the bridge reports for one bank (fixed at
the bridge, not with a new token) flags the connection and alerts once but keeps syncing, and clears
when a read comes back clean. Other failures retry quietly; the fourth in a row moves the connection
to `Error` and alerts.

**Disconnect:** SimpleFIN has no revoke call, so the user also revokes the app at the bridge. BuoyAI
forgets the access URL and deactivates the connection.

### Settings

| Key | Default | Meaning |
|---|---|---|
| `SimpleFin__Enabled` | on for `selfhost`, off for `hosted` | Offer SimpleFIN at all. When off, nothing is registered. |
| `SimpleFin__BridgeUrl` | `https://bridge.simplefin.org/simplefin/create` | Where the user makes a token. |
| `SimpleFin__AllowedHosts` | `simplefin.org` | Comma-separated domains the server will call. |
| `SimpleFin__MinSyncIntervalHours` | `6` | Least wait between syncs of one connection. |
| `SimpleFin__JitterMinutes` | `60` | Extra spread per connection. |
| `SimpleFin__InitialImportDays` | `90` | How far back the first sync asks. |

### API

`GET /api/aggregators` lists enabled providers (the app shows SimpleFIN only when `SimpleFin` is in
it). `POST /api/aggregators/SimpleFin/connect/complete` takes `{ "artifact": "<setup token>" }` and
answers `{ success, message }`. `GET /api/aggregators/connections` and
`DELETE /api/aggregators/connections/{id}` list and remove connections of any provider.

The Accounts page reads from provider-neutral routes that answer in every edition (the `/api/plaid/*`
routes are hosted only and stay for hosted and older apps, with the same answers):

| Route | Gives |
|---|---|
| `GET /api/bank/accounts` | The user's accounts (and their family's), any provider. |
| `GET /api/bank/connections` | Connections with their accounts and transaction counts. |
| `GET /api/bank/connections/{id}/import-progress` | Import progress of one of the user's connections. |
| `PUT /api/bank/accounts/{id}/nickname`, `PUT /api/bank/accounts/{id}/association` | Rename an account; choose whose budget it feeds. |
| `GET /api/bank/providers` | What Add a bank can offer right now: `Plaid` (hosted), `SimpleFin` (when enabled), `Bridge` (the Cloud bank feed, only once the server is paired). Any signed-in member may ask. |

## Encryption

The access URL is a bearer credential and is stored with `IEncryptionService` like Plaid's token. It
follows `Encryption:WriteV2` (see `docs/encryption_at_rest.md`): in the legacy format until that
switch is flipped, then converted by the re-encrypt job.
