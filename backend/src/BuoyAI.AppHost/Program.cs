var builder = DistributedApplication.CreateBuilder(args);

// ── Local agent fleet: per-slot isolation ──────────────────────────────────
// BUOY_SLOT (1..N) gives each parallel agent/worktree its own API port, database,
// so multiple stacks run concurrently. Slot 0/unset = legacy
// defaults (port 5000, database "BuoyAI") so existing scripts are unchanged.
// These formulas mirror scripts/dev/slot-env.sh — keep them in sync.
var slot = int.TryParse(Environment.GetEnvironmentVariable("BUOY_SLOT"), out var parsedSlot) && parsedSlot > 0
    ? parsedSlot
    : 0;
var apiPort = 5000 + slot * 100;
var dbName = slot > 0 ? $"BuoyAI_s{slot}" : "BuoyAI";
// Deterministic per-slot container names so dev-down.sh can remove exactly this
// slot's containers (Aspire orphans them on the forced kill we use on Windows) and
// concurrent slots never collide on a container name. Mirrors slot-env.sh.
var pgContainer = slot > 0 ? $"buoy-pg-s{slot}" : "buoy-pg";

// PostgreSQL database - managed by Aspire via Docker
// This will start a PostgreSQL container automatically.
// Resource name stays "BuoyAI" (the connection-string key the API resolves);
// the physical database name is per-slot so concurrent stacks don't collide.
// pgvector/pgvector:pg16 is stock Postgres 16 plus the vector extension, so embeddings (chat history,
// transaction similarity, help search) live in the same database. Same major version as hosted prod.
// The image is multi-arch (amd64 and arm64).
var postgresServer = builder.AddPostgres("postgres")
    .WithImage("pgvector/pgvector")
    .WithImageTag("pg16")
    .WithContainerName(pgContainer);
if (slot > 0)
{
    // Persistent lifetime lets the slot's container survive a backend restart so
    // Aspire REUSES it (dev-restart.sh) instead of failing on the existing name —
    // and the seeded DB is preserved. dev-down.sh removes it explicitly.
    postgresServer = postgresServer.WithLifetime(ContainerLifetime.Persistent);
}
else
{
    // pgAdmin is a convenience UI for manual/legacy dev only — skip it for fleet
    // slots to cut one container per stack.
    postgresServer = postgresServer.WithPgAdmin();
}
var postgres = postgresServer.AddDatabase("BuoyAI", dbName);

// Per-slot BuoyAI Connect (RFC #782) origin — pinned to this slot's actual API port.
string mcpIssuer = $"http://localhost:{apiPort}";
string mcpResourceUri = $"{mcpIssuer}/mcp";

// Backend API - references the database and exposes HTTP endpoints
var api = builder.AddProject<Projects.BuoyAI_API>("api")
    .WithReference(postgres)
    .WaitFor(postgres)  // Wait for PostgreSQL to be ready before starting API
    .WithHttpEndpoint(port: apiPort)
    .WithExternalHttpEndpoints()
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("VectorStore__Provider", "pgvector")
    // BuoyAI Connect (RFC #782): pin the OAuth token audience/issuer to THIS slot's real origin so a
    // Connect access token's aud matches the slot the MCP endpoint is actually served on. The
    // resource URI must be config-pinned (never derived from the request host), so each fleet slot
    // overrides it here by port instead of relying on the :5000 default in appsettings.Development.json.
    // Pre-formatted as plain strings so the (string, string) overload is used, not Aspire's
    // ReferenceExpression interpolation handler.
    .WithEnvironment("Mcp__ResourceUri", mcpResourceUri)
    .WithEnvironment("Mcp__Issuer", mcpIssuer);

// Flutter web frontend is run separately via `flutter run -d chrome --web-port 3000`
// Use start-flutter-dev.sh to launch both Aspire (infra + API) and Flutter together

builder.Build().Run();
