---
title: Run Buoy on your own server
description: Install the free self-hosted edition of Buoy with two files and Docker.
draft: true
sidebar:
  order: 1
---

The self-hosted edition of Buoy is free. It runs on a machine you own, and your budgets, transactions and goals live in a database on that machine.

## What you need

- Docker with the Compose plugin. On Windows and Mac that is Docker Desktop; on Linux, Docker Engine plus the `docker-compose-plugin` package. Check with `docker compose version`.
- A 64-bit computer that stays on: a PC, a NAS, or a Raspberry Pi 4 or 5 with 4 GB of memory and an SSD.
- About 2 GB of free disk to start. The download is under 1 GB.

On Windows, run the commands below in Git Bash or WSL. PowerShell can run them too, but the `curl` lines need `curl.exe` instead of `curl`.

## Install

Make a folder, download two files into it, and start Buoy:

```bash
mkdir buoy-money && cd buoy-money
curl -fsSLO https://github.com/BuoyMoney/buoy-money/releases/latest/download/compose.yaml
curl -fsSL  https://github.com/BuoyMoney/buoy-money/releases/latest/download/env.example -o .env
docker compose up -d
```

The first start downloads the image, which takes a minute or two on a home connection. After that Buoy is ready in about ten seconds. `docker compose ps` should show `app` and `db` as healthy.

Open `http://localhost:8080`, or the machine's address on your network (for example `http://192.168.1.20:8080`). The page says the server has no accounts yet. Fill in the form: that first account is the admin.

Once you are in, open **Accounts** and choose **Add a bank**. You can paste a SimpleFIN token, or choose **Import a file** and bring in a CSV, OFX or QFX file from your bank.

## Choose who the admin is

Every line in `.env` is optional, but one is worth setting before you open the page. Open `.env` in any text editor, add this line, and run `docker compose up -d` again:

```
BUOY_ADMIN_EMAIL=you@example.com
```

With it set, only that email can create the first account, and Buoy refuses anyone else until you have. Without it, the first person to open the page owns the server. That is fine on your own machine. If the machine is reachable from your network, register before you tell anyone the address.

After the first account, sign-up closes. The admin can open it again or make one-time invites. (Those two controls are server calls for now; the screen for them is not built yet. The exact commands are in the [sign-in and email reference](https://github.com/BuoyMoney/buoy-money/blob/main/docs/self_host/sign_in_email_push.md#who-can-register).)

The first start creates its own database password and encryption keys and keeps them in a Docker volume. You never type a password into a config file, and the database has no port open to your network.

## Use a different port

If something else already uses port 8080, set both of these in `.env`, then run `docker compose up -d`:

```
BUOY_PORT=8081
BUOY_ORIGIN=http://localhost:8081
```

Changing only the port is not enough. `BUOY_ORIGIN` is the address you type in the browser, and sign-in cookies, passkeys and email links check it. Use the same host and port you type.

## Updates

```bash
docker compose pull && docker compose up -d
```

Buoy backs up the database before it changes the database layout, and keeps the newest five backups. To stay on one version until you choose to move, put `BUOY_VERSION=` and a version number in `.env`. When you change it, run the two commands above to move.

Updates replace the image, not your `compose.yaml`. If a release's notes say the compose file changed, download it again over the old one; your `.env` and your data are untouched.

## Back up

Back up two things: the database, and the `secrets` volume that holds the encryption key. Keep them apart. A database copy without its key cannot read the stored bank tokens.

From the `buoy-money` folder:

```bash
# the database
docker compose exec -T db pg_dump -U buoy -d buoy --format=custom > buoy-$(date +%F).dump

# the keys (keep this file somewhere else, such as a password manager's file storage)
docker run --rm -v buoy-money_secrets:/secrets:ro alpine sh -c 'tar czf - -C /secrets .' > buoy-secrets.tgz
```

To restore a database dump, stop the app so nothing writes during the restore. Replace the file name with your dump:

```bash
docker compose stop app
docker compose exec -T db pg_restore -U buoy -d buoy --clean --if-exists --exit-on-error < buoy-2026-01-01.dump
docker compose start app
```

On a new machine, put the keys back before the first `docker compose up -d`: run `docker volume create buoy-money_secrets`, then `docker run --rm -i -v buoy-money_secrets:/secrets alpine sh -c 'tar xzf - -C /secrets' < buoy-secrets.tgz`. Compose will print a warning that the volume "was not created by Docker Compose". That is expected, and it uses your keys.

## Reach it from outside your home

Buoy can sit behind Tailscale so only your own devices can reach it, or behind Caddy for HTTPS on a name you own. Passkeys and email links need the address you type to match `BUOY_HOST` and `BUOY_ORIGIN` in `.env`, so set those before you invite anyone. Browsers only allow passkeys on `localhost` or over HTTPS.

For Caddy, download its settings file next to `compose.yaml`, set `BUOY_HOST` (your name, for example `buoy.example.com`), `BUOY_ORIGIN` (`https://` and the same name) and `BUOY_BIND=127.0.0.1` in `.env`, and start with the `https` profile:

```bash
mkdir -p caddy
curl -fsSL https://github.com/BuoyMoney/buoy-money/releases/latest/download/Caddyfile -o caddy/Caddyfile
docker compose --profile https up -d
```

Caddy needs ports 80 and 443 free on the machine. With a real domain that points at the machine and those ports reachable from the internet, it gets a free certificate by itself. If something else already uses 80 or 443, Tailscale is the easier route. The [running guide](https://github.com/BuoyMoney/buoy-money/blob/main/docs/self_host/running.md#https) has both setups step by step.

## Stop or remove it

```bash
docker compose down        # stops Buoy and keeps all your data
docker compose up -d       # starts it again, with everything as it was
docker compose down -v     # removes Buoy AND deletes your data and keys. This cannot be undone.
```

Take the backups above before `down -v`. To remove the image afterwards, run `docker image ls ghcr.io/buoymoney/buoy-money` and then `docker image rm` with the name and tag it lists.

## If something does not work

- `port is already allocated` or `address already in use`: another program uses that port. See "Use a different port".
- `Pool overlaps with other one on this address space`: another Docker network already uses `172.30.0.0/24`. Add `BUOY_WEB_SUBNET=172.29.0.0/24` and `BUOY_CADDY_IP=172.29.0.250` to `.env`.
- The page loads but sign-in or passkeys fail: the address in the browser does not match `BUOY_ORIGIN`.
- To see what Buoy is doing: `docker compose logs app --tail 100`.

## What the self-hosted edition includes

Budgets, savings goals, household sharing, the web app in any browser, file import, data export, manual entry, bank sync through SimpleFIN, and AI with your own provider or a model running on your machine. Bank sync through BuoyAI Cloud and AI with no setup are optional paid add-ons; see [BuoyAI Cloud products and prices](/docs/selfhost/cloud-products).
