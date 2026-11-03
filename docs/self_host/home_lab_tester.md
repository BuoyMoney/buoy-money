# Home-lab tester: setting up the test box

A cheap always-on box that runs Buoy Money the way a real user would, so we find the problems a
fast PC hides: slow disks, small memory, power cuts, a week of real bank syncs. The install itself
is in [running.md](running.md); this page is the box around it.

## The box

| Part | Pick | Why |
|---|---|---|
| Computer | Raspberry Pi 5 8 GB, or any mini PC (N100 class, 8 GB) | The Pi is the hardest realistic target. A mini PC is the easy case; test both if you can. |
| Disk | A USB 3 or NVMe SSD, 120 GB or more | The database writes constantly. An SD card is the thing most likely to die. |
| Power | The official 27 W supply for a Pi 5 | Weak supplies cause the odd resets that look like bugs. |
| OS | 64-bit: Raspberry Pi OS Lite (64-bit) or Debian 12 | The image has arm64 and amd64 builds only. |

Boot from the SSD (Pi: use Raspberry Pi Imager and pick the SSD as the target; no SD card).

## Set it up

1. Install Docker and the Compose plugin (<https://docs.docker.com/engine/install/debian/>), then
   `sudo usermod -aG docker $USER` and log in again.
2. Install Tailscale (<https://tailscale.com/download>), run `sudo tailscale up`, and in the
   Tailscale admin console turn on MagicDNS and HTTPS certificates.
3. Install Buoy Money with the two files (no clone):

   ```bash
   mkdir buoy-money && cd buoy-money
   curl -fsSLO https://github.com/BuoyMoney/buoy-money/releases/latest/download/compose.yaml
   curl -fsSL  https://github.com/BuoyMoney/buoy-money/releases/latest/download/env.example -o .env
   ```

4. Edit `.env`. Pin the version so upgrades happen when you choose, and put the app behind
   Tailscale:

   ```
   BUOY_VERSION=<version>
   BUOY_ADMIN_EMAIL=you@example.com
   BUOY_HOST=pi.your-tailnet.ts.net
   BUOY_ORIGIN=https://pi.your-tailnet.ts.net
   BUOY_BIND=127.0.0.1
   ```

5. Start it and put Tailscale in front:

   ```bash
   docker compose up -d
   tailscale serve --bg --https=443 http://127.0.0.1:8080
   ```

6. Add the trusted proxy line from the Tailscale section of [running.md](running.md#with-tailscale-recommended),
   or sign-in cookies lose their `Secure` flag.
7. Open `https://pi.your-tailnet.ts.net` on your phone (Tailscale installed there too), register
   with the admin email, and paste a SimpleFIN setup token (Accounts, Add a bank, SimpleFIN).

For AI, install Ollama on the same box, let Docker reach it, and follow [ai.md](ai.md). Ollama has no login, so do not bind it to `0.0.0.0` on a LAN
box: set `OLLAMA_HOST=172.17.0.1` (the `docker0` gateway address; check with `ip addr show docker0`), or
firewall port 11434 to the Docker subnets. A Pi
5 can run a 1B to 4B model slowly; it is fine for the categorization test and not much more.

## The test run

Run this from a clone on the box (or on any machine with Docker; the script builds or pulls its
own copy, with its own project name, port and network subnet, so it runs beside your real install):

```bash
git clone https://github.com/BuoyMoney/buoy-money.git && cd buoy-money
scripts/selfhost/check.sh --image ghcr.io/buoymoney/buoy-money --version <version> \
  --from-image ghcr.io/buoymoney/buoy-money:<previous-version> \
  --ollama http://host.docker.internal:11434/v1 --model qwen3:4b
```

Leave out `--image` to build from the checkout (slow on a Pi; build on a PC and push instead).
Leave out `--from-image` to test the sign-up lock and SimpleFIN on the new build. Set
`SIMPLEFIN_TOKEN` to a fresh setup token to include a real connect.

| Check | Script does it? |
|---|---|
| Fresh install, healthy, no published database port | Yes, with the time to healthy |
| Sign-up lock: first account is admin, a stranger gets 403, an invite lets one in | Yes |
| SimpleFIN connect: offered, bad tokens refused, real token if given | Yes (real token optional) |
| Power cut during an upgrade: kill the app during the upgrade, restart, data intact, backup kept | Mostly. The script reports SKIP when the kill missed the migration window; rerun or cut power by hand |
| Memory at idle against the limits | Yes, once. Repeat by hand as below |
| AI off by default, on with Ollama, model probe passes | Yes |
| A week of SimpleFIN syncing | No: leave the box running |
| A real power cut | No: pull the plug during `docker compose pull && docker compose up -d` |

## The week

Leave the real install running and check once a day:

```bash
docker stats --no-stream            # memory against the 768 MB and 384 MB limits
docker compose logs --since 24h app | grep -ci "error\|exception"
docker compose exec -T app ls /data/backups
df -h /var/lib/docker               # disk growth
```

Write down: memory after day 1 and day 7, sync times, any restart (`docker ps` shows uptime), and
anything that felt slow on the phone. Then pull the plug once during an upgrade and confirm it comes
back with your accounts intact.

Report problems as a GitHub issue.
