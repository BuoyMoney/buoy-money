# Contributing

This repository is a release mirror. The code is developed upstream by one maintainer, and each
release is published here as one commit. Pull requests opened here are read, and a change that is
accepted is reviewed and merged upstream, then appears in the next release with credit to the
author. The pull request itself is closed rather than merged. This keeps the copyright in one place,
which is what lets the same code run the free app, the hosted service and the store builds.

Bug reports, ideas and questions are welcome as issues.

## Report a bug

Open an issue with the app version, what you did, what you expected and what
happened. Remove real account numbers, names and tokens from logs and screenshots first.

## Report a security problem

Do not open an issue. Use the Security tab of this repository ("Report a vulnerability").

## Development

You need the .NET 10 SDK, Flutter 3.44 (stable) and Docker.

The quick way is one script. It starts the API with its own Postgres and Qdrant containers, writes a
local `appsettings.Development.json` with random keys, and starts the web app (add `--api-only` to
skip that). See [scripts/dev/README.md](scripts/dev/README.md).

```bash
scripts/dev/dev-up.sh
```

To do the same by hand:

```bash
# 1. A Postgres to talk to (the connection string in the example file points at localhost:5432)
docker run -d --name buoy-dev-pg -p 127.0.0.1:5432:5432 \
  -e POSTGRES_PASSWORD=dev -e POSTGRES_DB=BuoyAI postgres:15-alpine

# 2. Settings: copy the example, then set the Postgres password to "dev" and replace
#    Jwt:Key and Encryption:Key with random strings (the API refuses the placeholders)
cp backend/src/BuoyAI.API/appsettings.Example.json backend/src/BuoyAI.API/appsettings.Development.json

# 3. The API (applies the database migrations on start)
cd backend && dotnet run --project src/BuoyAI.API --urls http://localhost:5000

# 4. The app, in a second terminal (the legal texts are bundled as assets)
cd flutter && mkdir -p assets/legal && cp ../legal/*.md assets/legal/
flutter pub get && flutter run -d chrome
```

Qdrant is only used by the AI features. To keep those embeddings in the app's own Postgres instead,
set `VectorStore:Provider` to `pgvector`.

What works without keys: budgets, goals, subscriptions, manual accounts and file import, on the
web with no secrets. What stays off until you add your own keys to
`appsettings.Development.json`: bank sync (Plaid or SimpleFIN), AI features (a Gemini key or a local
model), Stripe billing and Google or Apple sign-in. Mobile push needs
your own Firebase project: replace the placeholder `flutter/android/app/google-services.json` and
`flutter/lib/firebase_options.dart` with yours.

## Check your change

- Backend: `cd backend && dotnet build && dotnet test tests/BuoyAI.UnitTests`. The integration
  tests need Docker.
- App: `cd flutter && flutter analyze && flutter test`.
- Website: `cd marketing && npm ci && npm run build`.

The same checks run on every pull request (see `.github/workflows/ci.yml`).
