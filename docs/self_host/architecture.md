# Buoy Money: architecture

> The Edition setting, the self-host image and the version endpoint described in the first
> sections exist. The Cloud services are described here as designed; where this file names
> code, it names code that exists. Prices are on the website: <https://buoy-ai.com/founding>.

## One codebase, two editions

`BuoyMoney:Edition` (`hosted` or `selfhost`; unset means hosted) decides which services register
at startup. It is read once into `EditionInfo`
(`backend/src/BuoyAI.Infrastructure/Configuration/EditionInfo.cs`), and every gate asks that one
object. A value that is set but misspelled stops the app at startup rather than guessing.

Hosted keeps everything it has now. Selfhost:

- registers no Stripe, RevenueCat, billing, referral, reservation, broadcast or Plaid service
  (`DependencyInjection.cs`), so `StripeService` and `PlaidService`, which throw in their
  constructors without keys, cannot be reached;
- registers `SelfHostEntitlementService` in place of `EntitlementService` and its Stripe hook:
  every account is Premium, and addresses on `Admin:Emails` are Admin;
- skips the ten hosted-only background jobs, which are one block in `DependencyInjection.cs`;
- answers 404 from the hosted-only controllers through the `[HostedOnly]` resource filter
  (`backend/src/BuoyAI.API/Filters/HostedOnlyAttribute.cs`), which runs before the controller
  is built;
- gets its own defaults, as the lowest-priority configuration source, from `EditionDefaults.cs`:
  pre-migration backup on, v2 encryption on, HTTPS redirect off, and forwarded headers trusted
  only from loopback and explicitly configured proxy addresses;
- reads generated secrets from `BuoyMoney:SecretsDirectory` (one file per setting);
- logs to the console only.

Sign-in, account deletion and data retention used to need `PlaidService` to be constructible;
they now resolve it on use and skip it when it is absent. How to run it: [running.md](running.md).

## What self-host drops or replaces

| Today (in the repo) | In self-host |
|---|---|
| Stripe billing: `backend/src/BuoyAI.Infrastructure/Services/StripeService.cs` | Off. Cloud products are bought on the web and checked through the Cloud account. |
| RevenueCat: `backend/src/BuoyAI.Infrastructure/Services/RevenueCatSyncService.cs` | Off. |
| Plaid-direct: `PlaidService.*.cs` in `backend/src/BuoyAI.Infrastructure/Services/` | Off. Bank data comes from SimpleFIN or the Cloud bank feed through `IBankAggregator`. |
| Marketing site and the combined nginx image | Not shipped. One app container plus Postgres. |
| Hosted-only background jobs (`AddHostedService` and `AddJobLockedHostedService` registrations in `backend/src/BuoyAI.Infrastructure/DependencyInjection.cs`) | Each one gets a gate. Which ones are hosted-only is settled by the registrations in that file. |
| Qdrant for embeddings (`ChatEmbeddingService`, `TransactionEmbeddingService`, `HelpContentService`, `QdrantCollectionInitService`, all in `backend/src/BuoyAI.Infrastructure/Services/`) | pgvector inside Postgres. |
| Gemini behind `IGeminiApiClient` (`backend/src/BuoyAI.Core/Interfaces/IGeminiApiClient.cs`) | Any OpenAI-compatible provider, or no AI. AI is off by default. |
| Google sign-in, FCM push, Resend email | Email and password plus authenticator app as the baseline. Email through any SMTP server is optional. Push through ntfy or UnifiedPush is optional. |

### Embedding size

Three services hard-code 768 dimensions (`EmbeddingDimensions` in `ChatEmbeddingService.cs`,
`HelpContentService.cs` and `TransactionEmbeddingService.cs`, all in
`backend/src/BuoyAI.Infrastructure/Services/`). The pgvector move stores
the embedding model and dimension per row so a provider change does not break old rows.

## Bank providers

`IBankAggregator` (`backend/src/BuoyAI.Core/Interfaces/IBankAggregator.cs`) is the seam, with
`IBankAggregatorRegistry` dispatching on the `BankProvider` enum
(`backend/src/BuoyAI.Core/Enums/BankProvider.cs`). Plaid is the only real implementation today.
SimpleFIN and a Cloud feed provider are the other providers.

## BuoyAI Cloud services

Cloud runs separately from the hosted app, so a problem in one cannot take down the other.

- **Bank feed bridge.** Pairing, a BuoyAI-hosted bank login page, a token vault, delivery to
  the user's server. Details below.
- **AI gateway.** A metered proxy with the monthly allowance, opt-in pay-as-you-go, a user cap
  and a usage meter. Today's `AIUsageRecord`
  (`backend/src/BuoyAI.Core/Entities/AIUsageRecord.cs`) already records cost per call and is
  the starting point for the meter.
- **Entitlements.** `bank_sync` and `ai`, building on the cross-provider entitlement model
  in the backend (`backend/src/BuoyAI.Core/Entitlements/`).

## Bank feed bridge: data flow

Design rule: the account owner exports their own data to their own server.

1. The owner links their self-hosted server to their Cloud account. Each server gets its own
   revocable credential and its own public key.
2. The owner connects a bank on a BuoyAI-hosted page, under owner-only and login-cap rules
   (bank login runs on a BuoyAI-hosted page because Plaid requires it for OAuth banks).
3. Access tokens sit in a key vault on the Cloud side. Nothing else about the user's accounts
   is kept.
4. When data is due, the bridge fetches it and encrypts it to the server's public key, so only
   that server can read it. It is delivered only to servers paired with BuoyAI.
5. After delivery the bridge keeps nothing but what the vault needs to fetch the next update.
6. Controls: rate limits, refresh counting, an audit log, a kill switch and a minimum server
   version.

The wire contract and the cursor rules are in [bridge.md](bridge.md).

## Version gate

Built:

- `GET /api/system/version` (anonymous) reports `version`, `minClientVersion`
  (`System:MinClientVersion`, default 1.0.0) and `edition`.
- The app checks it at startup (`flutter/lib/services/server_version_service.dart`). A server below
  the app's `AppConfig.minServerVersion` shows "Your server needs an update"; a server whose
  `minClientVersion` is above the app shows "This app needs an update". Any doubt (offline, a
  server without the endpoint, a local build) lets the app through, so hosted users are never
  locked out by a missed handshake.

Not built yet:

- A minimum server version enforced by the Cloud bridge, so an out-of-date server loses the
  feed until it updates.

## Database upgrades on machines we can't reach

Upgrades run through `backend/src/BuoyAI.Infrastructure/Data/SqlMigrationRunner.cs` over the
SQL scripts in `backend/src/BuoyAI.Infrastructure/Migrations/Sql/`. A pre-migration backup is on by default in self-host; see [running.md](running.md) for upgrades.
