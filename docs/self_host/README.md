# Buoy Money (self-host)

> The self-host edition runs as one container plus Postgres; see [running.md](running.md). The
> files below describe how it works and how to run it. Where a file describes something not
> built yet, it says so.

## What Buoy Money is

Buoy Money is a free, open-source budgeting app that people run on their own server, plus
optional paid BuoyAI Cloud products for the parts a home server cannot do alone (a managed bank
feed, AI with no setup). More Cloud products may come later. The hosted app at buoy-ai.com runs
on the same code.

## Editions

| Edition | Who runs it | What it is |
|---|---|---|
| `hosted` | BuoyAI | Today's app at buoy-ai.com. Keeps Stripe, RevenueCat, Plaid-direct and the marketing site. |
| `selfhost` | The user | The same code with the hosted-only pieces switched off. Free. Runs from `selfhost/compose.yaml`. |

How the split works: [architecture.md](architecture.md).

## Free app vs BuoyAI Cloud

The self-hosted app is free: budgets, goals, household sharing, native apps, file import, data
export, manual entry, SimpleFIN, and bring-your-own or local AI. BuoyAI Cloud sells separate
products on top (bank sync, AI). Prices and what each product includes are on the website:
<https://buoy-ai.com/founding> and the [Cloud products page](https://buoy-ai.com/docs/selfhost/cloud-products/).

## Files in this folder

| File | What it holds |
|---|---|
| [ai.md](ai.md) | Choosing an AI provider (Ollama, OpenAI, OpenRouter, Gemini, none), what works with which, the settings screen, and the categorization benchmark. |
| [running.md](running.md) | How to install, back up, upgrade and add HTTPS to a self-hosted install; what the edition switches off; known limits. |
| [home_lab_tester.md](home_lab_tester.md) | Setting up the Raspberry Pi or mini PC test box, and the `scripts/selfhost/check.sh` checklist that runs the scriptable stage 3 tests. |
| [sign_in_email_push.md](sign_in_email_push.md) | Sign-in methods, single sign-on (Pocket ID, Authentik, Keycloak), the registration lock and invites, email through SMTP, and phone notifications. |
| [bridge.md](bridge.md) | Pairing a server with a BuoyAI Cloud account, the Cloud bank feed (Plaid behind the Cloud, encrypted batches, cursor after ack), and its wire contract. |
| [connect.md](connect.md) | Tools for AI assistants: Open WebUI (or another MCP client) reading your data at /mcp with a personal access token, and Buoy's chat using Open WebUI's models. |
| [push_relay.md](push_relay.md) | The wire contract between a self-hosted server, the BuoyAI push relay and the phone, including the encryption format. |
| [architecture.md](architecture.md) | Editions, what self-host drops, the Cloud services, the bank feed data flow, the version gate. |
