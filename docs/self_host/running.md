# Buoy Money: running it yourself

How to install, back up, upgrade and put HTTPS in front of a self-hosted Buoy Money. The
code split is in [architecture.md](architecture.md).

You need Docker with the Compose plugin, a 64-bit machine (a PC, a NAS, or a Raspberry Pi 4 or
5 with 4 GB of memory and an SSD), and about 2 GB of disk to start.

## Install

Two files, no clone:

```bash
mkdir buoy-money && cd buoy-money
curl -fsSLO https://github.com/BuoyMoney/buoy-money/releases/latest/download/compose.yaml
curl -fsSL  https://github.com/BuoyMoney/buoy-money/releases/latest/download/env.example -o .env
docker compose up -d
```

Every line in `.env` is optional and commented out. Open <http://localhost:8080> (or the machine's
address on your network), register, and you are in. The first account you register with the email
in `BUOY_ADMIN_EMAIL` is the admin, so set that in `.env` first if you want one.

`docker compose up -d` pulls the published image (Intel/AMD and ARM, so it also runs on a
Raspberry Pi). The image tag defaults to `latest`; put `BUOY_VERSION=<version>` in `.env` to stay on
one release until you choose to move.

Each published image carries a signed build provenance record and an SBOM. To check that an image
was built from this repository, run
`gh attestation verify oci://ghcr.io/buoymoney/buoy-money:<version> --owner BuoyMoney`.

To build the image yourself instead, clone the repository and run it from the checkout:

```bash
git clone https://github.com/BuoyMoney/buoy-money.git && cd buoy-money/selfhost
docker compose up -d --build
```

## What first run does for you

An `init` service runs once before anything else and writes the database password, the app's
signing key, the encryption key and the database connection string into a Docker volume called
`secrets`. Nothing in the compose file is a password, and there are no placeholder keys to forget
to change. It never overwrites a secret that exists.

The database is on an internal network with no published port and no route to the internet. Only
the app listens on a port.

## Settings

All optional, in `selfhost/.env`:

| Setting | Default | What it does |
|---|---|---|
| `BUOY_VERSION` | `latest` | Which release to run. Pin a number to upgrade on purpose. |
| `BUOY_HOST` | `localhost` | The host name you type in the browser. This is the passkey "relying party"; changing it later invalidates every passkey. |
| `BUOY_ORIGIN` | `http://localhost:8080` | The full address (scheme, host, port) you type. Must match exactly or passkeys and email links break. |
| `BUOY_ADMIN_EMAIL` | none | The account with this email is the admin. |
| `BUOY_PORT` | `8080` | The port the app listens on. Change `BUOY_ORIGIN` to match (for example `http://localhost:8081`), or sign-in cookies, passkeys and email links check the wrong address. |
| `BUOY_BIND` | `0.0.0.0` | Which interface it listens on. Use `127.0.0.1` when Caddy or Tailscale on the same machine is the front door. |
| `BUOY_SMTP_HOST`, `_PORT`, `_SECURITY`, `_USERNAME`, `_PASSWORD`, `_FROM` | none | Outgoing email through any SMTP server. See [sign_in_email_push.md](sign_in_email_push.md). |
| `BUOY_PUSH_RELAY_URL` | none | The push relay used for phone notifications. See [sign_in_email_push.md](sign_in_email_push.md). |

AI is off until you choose a provider; see [ai.md](ai.md) for the `BUOY_AI_*` settings.

The product name shown in emails, push text, the assistant connector and your authenticator app is
"Buoy Money". To show your own name instead, set `Brand__Name` in the `app` service's `environment:`
(see below). The app on your phone is always called Buoy Money.

Anything else the app reads can be set in the `app` service's `environment:` in `compose.yaml`
(a double underscore stands for a colon, so `Gemini__ApiKey` sets `Gemini:ApiKey`).

## Where your data lives, and backups

| Volume | What is in it | Back it up? |
|---|---|---|
| `pgdata` | The database. | Yes. |
| `secrets` | The database password and the encryption key. | Yes, and keep it separate from the database dump. A dump without the encryption key cannot read the stored tokens. |
| `appdata` | Automatic pre-upgrade database backups in `backups/` (the newest five). | Optional. |
| `caddy_data` | Certificates, only with the `https` profile. | No. |

A backup you can restore with one command:

```bash
docker compose exec -T db pg_dump -U buoy -d buoy --format=custom > buoy-$(date +%F).dump
docker compose stop app
docker compose exec -T db pg_restore -U buoy -d buoy --clean --if-exists --exit-on-error < buoy-2026-01-01.dump
docker compose start app
```

Stop the app first so nothing writes during the restore.

Back up the keys the same way (the volume is named after the compose project, `buoy-money_secrets`).
The archive goes to standard output, so no host folder is mounted and Git Bash on Windows does not
rewrite the paths:

```bash
docker run --rm -v buoy-money_secrets:/secrets:ro alpine sh -c 'tar czf - -C /secrets .' > buoy-secrets.tgz
```

On a new machine, restore them before the first `docker compose up -d`. Compose warns that the volume
"was not created by Docker Compose"; that is expected.

```bash
docker volume create buoy-money_secrets
docker run --rm -i -v buoy-money_secrets:/secrets alpine sh -c 'tar xzf - -C /secrets' < buoy-secrets.tgz
```

## Stopping and removing

`docker compose down` stops Buoy and keeps every volume, so `docker compose up -d` brings it back as it
was. `docker compose down -v` also deletes the volumes (the database and the keys). Back up first.

## Upgrades

```bash
docker compose pull && docker compose up -d
```

If you pinned `BUOY_VERSION` in `.env`, change it to the version you want first.

Before the app changes the database it takes a `pg_dump`, checks that `pg_restore` can read it,
and refuses to continue if it cannot. The dump is in the `appdata` volume under `backups/`. This
is on by default in the self-host edition (`Migrations:BackupBeforeMigrate`). If a backup keeps
failing and you accept the risk, `Migrations__AllowMigrateWithoutBackup=true` lets the upgrade
proceed.

The app and the server check each other's version at startup. If you open a new app against an
old server, it says "Your server needs an update" and tells you the version it needs. If the
server has dropped support for an old app, the app says so instead.

Do not run two copies of the app against one database.

## HTTPS

Plain HTTP on your own network works for everything except passkeys, which browsers only allow on
`localhost` or over HTTPS with a stable name. Passwords with an authenticator app work either way.

Your sign-in survives a page reload over plain HTTP too: the app sets its sign-in cookie with the
`Secure` flag only when the request came in over HTTPS (or through a proxy that says so with
`X-Forwarded-Proto`), so a browser does not drop it on `http://localhost:8080` or a LAN address. The
cookie is named `buoy_money_refresh`, so another Buoy server on the same host name (a different
port counts as the same host for cookies) does not overwrite or clear your session. Over plain HTTP
the cookie travels unencrypted, so HTTPS (Tailscale or Caddy below) is still the recommended setup
for anything beyond your own machine or a network you fully trust.

### With Tailscale (recommended)

Gives a real certificate and a stable name, reachable from your phone anywhere, with no port
forwarding. In the Tailscale admin console turn on MagicDNS and HTTPS certificates, then on the
machine running Buoy Money:

```bash
tailscale serve --bg --https=443 http://127.0.0.1:8080
```

and in `.env`:

```
BUOY_HOST=your-machine.your-tailnet.ts.net
BUOY_ORIGIN=https://your-machine.your-tailnet.ts.net
BUOY_BIND=127.0.0.1
```

Every device that uses it needs Tailscale installed.

Tailscale reaches the app through Docker's gateway address, which the app does not trust by
default, so it ignores `X-Forwarded-Proto` and the sign-in cookie would be issued without the
`Secure` flag. Fix that once: add the
gateway of the compose `web` network (172.30.0.1 with the bundled subnet) under `environment:` for
the app in `compose.yaml`. Docker Desktop and rootless Docker may show a different address; the
app's startup log shows the client address it sees.

```
ForwardedHeaders__TrustedNetworks__3: 172.30.0.1/32
```

This is safe because `BUOY_BIND=127.0.0.1` means only processes on this machine can reach the app.

### With Caddy

Caddy needs one more file next to `compose.yaml`:

```bash
mkdir -p caddy
curl -fsSL https://github.com/BuoyMoney/buoy-money/releases/latest/download/Caddyfile -o caddy/Caddyfile
docker compose --profile https up -d
```

Set `BUOY_HOST` and `BUOY_ORIGIN` first, and `BUOY_BIND=127.0.0.1`. Caddy publishes ports 80 and 443
on the machine, so both must be free; if something else uses them, use Tailscale instead. With a real domain that points
at this machine and ports 80 and 443 reachable, Caddy gets a free public certificate by itself.
With a home name (`buoy.home.arpa`, `buoy.local`) Caddy signs with its own authority, and each
device has to trust it once. Phones make that hard; Tailscale avoids it.

The app trusts the `X-Forwarded-*` headers only from loopback and the bundled Caddy container
(fixed address 172.30.0.250). Private ranges are not trusted, because Docker Desktop and rootless
Docker show every inbound client as a private gateway address. If your proxy sits somewhere else,
set `ForwardedHeaders__TrustedNetworks__3` (and `__4`, and so on) to its address. Behind
`tailscale serve` the app sees the Docker gateway as the client, so all tailnet devices share one
rate-limit bucket; that is safe, just coarse.

## Small machines

The image runs .NET with workstation garbage collection and conservative memory settings, and the
compose file caps the app at 768 MB, the database at 384 MB and Caddy at 96 MB. Measured numbers
for this build are in the "Measured" section at the bottom of this file. Use an SSD rather than an
SD card (the database writes constantly), and give a 4 GB board a little swap.

## What is switched off in the self-host edition

`BuoyMoney__Edition=selfhost` (set in the image) turns off everything that exists only for the
hosted service:

- Stripe, RevenueCat, referrals, founding-member reservations, the marketing lead form and admin
  broadcast email: those endpoints answer 404 and their services are never registered. The one
  exception is the lead service itself, which stays registered because sign-up code depends on
  it; nothing can reach it, since its endpoint is switched off and the lead emails do not run.
- Direct Plaid: no Plaid account is needed or used. Bank data arrives through SimpleFIN (paste a
  setup token in Settings; see `docs/bank_providers.md`), which is on by default here. Set
  `SimpleFin__Enabled=false` to turn it off. A paid BuoyAI Cloud account can add a managed Plaid
  feed (`CloudBridge__Url` and `CloudBridge__BankFeedEnabled`; see [bridge.md](bridge.md)), which is off until you set both.
- The hosted background jobs: trial and lead emails, referral rewards, revenue gauges, nightly
  cross-user statistics. The bank sync job still runs, to pull SimpleFIN a few times a day.
- Plans: every account has every feature. There is no trial and no paywall.
- Sign-up: the first account is the admin and then registration closes. See
  [sign_in_email_push.md](sign_in_email_push.md).
- Disk log files: the app logs to the console, which Docker rotates.
- Third-party scripts in the web page (Google, Apple, Plaid, PostHog), and the Sentry error
  reporter unless you set its address yourself.

## Known limits

- Without an SMTP server there is no email, so there is no self-service password reset. Keep the
  authenticator app's recovery codes, or set up email ([sign_in_email_push.md](sign_in_email_push.md)).
- Embedding search (chat memory and similar-transaction categorization) needs Qdrant, which this
  compose file does not run. It moves into Postgres in a later release.
- Mobile apps need a way to point at your server's address; the web app works today.
- App store listings (Umbrel, CasaOS, Unraid) are not part of this release. They will live in a
  separate folder next to this one and pin the image by digest.

## Building the image yourself

```bash
# This machine's architecture:
docker build -f Dockerfile.selfhost -t buoy-money .
# A PC and a Raspberry Pi in one image:
docker buildx build --platform linux/amd64,linux/arm64 -f Dockerfile.selfhost -t your/registry/buoy-money --push .
```

The Flutter and .NET builds run once on the build machine and produce files that do not depend on
the CPU, so only the small runtime layer is built for each architecture.

## Measured

From a real run of this build on a PC (Docker Desktop, amd64), checked 2026-10-12, right after first
start with one registered account and no bank data:

| Part | Memory at idle |
|---|---|
| App (API and web app) | about 150 MB of its 768 MB limit |
| Postgres | about 52 MB of its 384 MB limit |

First start (empty database, 52 migrations) took about 10 seconds to a healthy app. A pre-upgrade
backup of the fresh database was written and verified in under a second (about 300 KB).
`main.dart.js` is 6.4 MB and goes over the wire as 1.8 MB gzip. These are PC numbers, not Raspberry
Pi numbers; the Pi soak test is still to do (see [home_lab_tester.md](home_lab_tester.md)).
