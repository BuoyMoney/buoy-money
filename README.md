# Buoy Money

[![CI](https://github.com/BuoyMoney/buoy-money/actions/workflows/ci.yml/badge.svg)](https://github.com/BuoyMoney/buoy-money/actions/workflows/ci.yml)

A budgeting app you run on your own server. Free and open source. It keeps your transactions,
budgets, goals and subscriptions in a database you control.

Optional paid BuoyAI Cloud products (a managed bank feed, AI with no setup) are
sold separately at <https://buoy-ai.com>. The app works without them.

## Run it

You need Docker with the Compose plugin. Two files, no clone:

```bash
mkdir buoy-money && cd buoy-money
curl -fsSLO https://github.com/BuoyMoney/buoy-money/releases/latest/download/compose.yaml
curl -fsSL https://github.com/BuoyMoney/buoy-money/releases/latest/download/env.example -o .env
docker compose up -d
```

Open <http://localhost:8080>. Full guide: [docs/self_host/running.md](docs/self_host/running.md).

## What is here

| Folder | What it holds |
|---|---|
| `backend/` | The .NET 10 API and its tests. |
| `flutter/` | The app (web, Android, iOS). |
| `marketing/` | The public website. |
| `selfhost/` | The compose file, its example settings and the Caddy HTTPS config. |
| `docs/self_host/` | Install, upgrade, backup, AI, email and push guides. |


## Maintainer

Jonathan Barnell. More about me and the project: <https://buoy-ai.com/about>.

## Contributing

This repository is a release mirror: see [CONTRIBUTING.md](CONTRIBUTING.md).

## Security

Report problems privately: see [SECURITY.md](SECURITY.md).

## License

Buoy Money is free software under the GNU Affero General Public License, version 3 only
(AGPL-3.0-only). See [LICENSE](LICENSE). You can run it, change it and share it. If you run a
changed version as a service for other people, you must offer them your changed source code.

The license covers the code. It does not cover the Buoy Money or BuoyAI names and logos.
