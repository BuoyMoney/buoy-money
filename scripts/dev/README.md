# Dev scripts

Scripts for running Buoy Money on your own machine while you work on it.

## Dev slots

A dev slot is one isolated copy of the stack: the API, its own Postgres database and its own
Qdrant container, plus the Flutter web app unless you ask for the API only. Slot `N` uses
these ports, so several checkouts (or several runs of the tests) never collide:

| Part | Slot 0 (default) | Slot N (1 and up) |
|---|---|---|
| API | 5000 | 5000 + N * 100 |
| Flutter web | 3000 | 3000 + N * 100 |
| Aspire dashboard | 17111 | 18000 + N * 10 |
| Database | `BuoyAI` | `BuoyAI_sN` |

Each git worktree keeps the same slot between runs. The formulas live in `slot-env.sh`; the
containers are started by the Aspire host in `backend/src/BuoyAI.AppHost`.

```bash
scripts/dev/dev-up.sh --api-only   # API + Postgres + Qdrant for the first free slot
scripts/dev/dev-up.sh              # the same, plus Flutter web
scripts/dev/dev-status.sh          # every slot and what is running
scripts/dev/dev-restart.sh         # restart the API after editing C# (Flutter hot-reloads on its own)
scripts/dev/reset-db.sh            # put the slot's database back to fresh migrations + seed data
scripts/dev/dev-down.sh            # stop the slot and free it
```

Needs Docker, the .NET 10 SDK and, for the web app, Flutter. The scripts are written for Git Bash
on Windows and also run on Linux and macOS.

## Other scripts

| Script | What it does |
|---|---|
| `ci-local.sh` | Runs what CI runs, in order, so you find a failure before you push. |
| `ui-smoke.sh`, `overflow-sweep.sh` | Drive the real web app against a slot (login flow, layout overflow sweep). Need chromedriver. |
| `migration-upgrade-check.sh` | Runs the migration lint and the upgrade tests against a throwaway Postgres. |
