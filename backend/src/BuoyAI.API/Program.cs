using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Serilog;
using BuoyAI.API.Endpoints;
using BuoyAI.API.Extensions;
using BuoyAI.API.Filters;
using BuoyAI.API.Middleware;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Data;
using System.Runtime.CompilerServices;
using System.Security.Claims;
[assembly: InternalsVisibleTo("BuoyAI.IntegrationTests")]



var builder = WebApplication.CreateBuilder(args);

// The product name shown in emails, push text, the MCP server and the authenticator app (default Buoy Money).
BuoyAI.Core.Configuration.Brand.Configure(builder.Configuration["Brand:Name"]);

// Which edition is this? BuoyMoney:Edition = hosted (default, today's behaviour) or selfhost.
// Self-host adds its defaults (pre-migration backup, v2 encryption, forwarded-header trust) and its
// generated secrets as the LOWEST priority sources, so anything set explicitly still wins.
var edition = BuoyAI.Infrastructure.Configuration.EditionDefaults.Apply(builder.Configuration, builder.Configuration);

// Configure Sentry for error tracking (only if DSN is provided)
// Empty string or null DSN disables Sentry
var sentryDsn = builder.Configuration["Sentry:Dsn"];
if (!string.IsNullOrEmpty(sentryDsn))
{
    builder.WebHost.UseSentry(o =>
    {
        // A SimpleFIN setup token and a Cloud link code are secrets carried in the URL path: drop breadcrumbs
        // for those calls, and redact the code from the request URL of any error or transaction.
        o.SetBeforeBreadcrumb(b =>
            b.Data != null && b.Data.TryGetValue("url", out var url) && BuoyAI.Core.Utilities.SecretPathRedaction.CarriesSecret(url)
                ? null
                : b);
        o.SetBeforeSend(e =>
        {
            e.Request.Url = BuoyAI.Core.Utilities.SecretPathRedaction.Redact(e.Request.Url);
            return e;
        });
        o.SetBeforeSendTransaction(t =>
        {
            t.Request.Url = BuoyAI.Core.Utilities.SecretPathRedaction.Redact(t.Request.Url);
            return t;
        });
    });
}

// Add Aspire service defaults (OpenTelemetry, health checks, resilience)
builder.AddServiceDefaults();

// Vector store for AI chat history, transaction similarity and help search.
// VectorStore:Provider = qdrant (default, hosted) | pgvector (self-host and dev: the app's own Postgres).
var vectorProvider = BuoyAI.Infrastructure.Services.VectorStoreProvider.Resolve(builder.Configuration["VectorStore:Provider"]);
if (vectorProvider == BuoyAI.Infrastructure.Services.VectorStoreProvider.PgVector)
{
    Log.Information("Vector store: pgvector (embeddings live in the application database)");
}
else
{
    // Only register the Qdrant client if a connection string is configured (optional for VPS deployment).
    var qdrantConnectionString = builder.Configuration.GetConnectionString("qdrant");
    if (!string.IsNullOrEmpty(qdrantConnectionString))
    {
        builder.AddQdrantClient("qdrant");
        Log.Information("Qdrant vector database configured"); // not the value: the string can carry an ApiKey
    }
    else
    {
        Log.Warning("Qdrant connection string not configured. AI chat history semantic search will be disabled.");
    }
}

// Configure Serilog with file logging for debugging
// Determine logs path based on environment
string logsPath;
if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
{
    // In Docker, use /tmp/logs (writable in most containers)
    logsPath = "/tmp/logs";
}
else
{
    // Local development - use relative path from assembly location
    logsPath = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "logs");
    logsPath = Path.GetFullPath(logsPath);
    // Fallback to a known location if the calculated path doesn't exist or is invalid
    if (!logsPath.Contains("BuoyAI") || logsPath.Length < 10)
    {
        logsPath = @"C:\BuoyAI\logs";
    }
}

try
{
    Directory.CreateDirectory(logsPath);
}
catch (Exception ex)
{
    // If we can't create the logs directory, just use console logging
    Console.WriteLine($"Warning: Could not create logs directory at {logsPath}: {ex.Message}");
    logsPath = Path.GetTempPath();
}

var loggerConfiguration = new LoggerConfiguration()
  .ReadFrom.Configuration(builder.Configuration)
  .MinimumLevel.Information()
  .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
  .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
  .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
  .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
  .MinimumLevel.Override("Sentry", Serilog.Events.LogEventLevel.Warning)
  // Polly v8's standard resilience handler (AddStandardResilienceHandler) emits an
  // "ExecutionAttempt" telemetry event for EVERY outbound HttpClient attempt at
  // Information — including successful first tries (Attempt 0, Handled: False). That
  // floods prod with ~1 line per telemetry export. Warning keeps genuine retries and
  // circuit-breaker events while dropping the routine success noise.
  .MinimumLevel.Override("Polly", Serilog.Events.LogEventLevel.Warning)
  // Debug for hosted (unchanged); self-host keeps the console readable on a home box.
  .MinimumLevel.Override("BuoyAI", edition.IsHosted ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information)
  .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{NewLine}{Message:lj}{NewLine}{Exception}");

// Hosted also writes daily log files. Self-host logs to the console only: its container's /tmp is
// RAM-backed, a month of rolling files there would eat memory a small box needs, and `docker logs`
// (rotated by the compose file) is where a home user looks.
if (edition.IsHosted)
{
  loggerConfiguration.WriteTo.File(
    Path.Combine(logsPath, "api-.log"),
    rollingInterval: RollingInterval.Day,
    shared: true,  // Allow multiple processes to write
    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}{NewLine}{Message:lj}{NewLine}{Exception}");
}

Log.Logger = loggerConfiguration.CreateLogger();

// Log where we're writing to
if (edition.IsHosted) Log.Information("API logs will be written to: {LogsPath}", logsPath);
Log.Information("Edition: {Edition}", edition.Name);

builder.Host.UseSerilog();

builder.Services.AddHttpContextAccessor();

// Add services to the container.
builder.Services.AddControllers()
  .AddJsonOptions(options =>
  {
    options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.JsonSerializerOptions.DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
  })
  .AddDebugEndpointGate(builder.Environment, builder.Configuration); // /api/debug/* only where allowed

// Add Response Caching for static/cacheable endpoints
builder.Services.AddResponseCaching();

// Add Infrastructure services (the edition read above is registered first so both agree)
builder.Services.AddSingleton(edition);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSingleton<BuoyAI.API.Services.ExportConcurrencyGate>(); // global cap on concurrent data exports

// Add database health check (in addition to the default service health checks)
// Only add NpgSql health check if we have a connection string (skip for in-memory test database)
var connectionString = builder.Configuration.GetConnectionString("BuoyAI") 
    ?? builder.Configuration.GetConnectionString("DefaultConnection");

if (!string.IsNullOrEmpty(connectionString) && !connectionString.Contains("InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHealthChecks()
        .AddNpgSql(
            connectionString, 
            name: "postgresql",
            tags: new[] { "db", "sql", "postgresql" },
            timeout: TimeSpan.FromSeconds(5));
}

// Configure Rate Limiting with tiered per-user and per-IP policies
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    
    // Global rate limit: Per-IP fallback for unauthenticated requests
    // 50 requests per minute per IP (stricter for anonymous)
    options.AddPolicy("global", httpContext =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: $"ip:{BuoyAI.API.RateLimitKeys.ClientIp(httpContext, edition.IsSelfHost)}",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 50,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 4, // Smoother limiting with 15-second segments
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 5
            }));
    
    // Strict rate limit for auth endpoints: 10 requests per minute per IP
    // Always IP-based to prevent brute force attacks
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"auth:{BuoyAI.API.RateLimitKeys.ClientIp(httpContext, edition.IsSelfHost)}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 2
            }));
    
    // API rate limit: Tiered by authentication status and subscription tier
    // Authenticated users get per-user limits; anonymous gets per-IP
    options.AddPolicy("api", httpContext =>
    {
        // Check if user is authenticated
        var userId = httpContext.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        
        if (!string.IsNullOrEmpty(userId))
        {
            // Get subscription tier from claims (set during JWT creation)
            var tierClaim = httpContext.User?.FindFirst("subscription_tier")?.Value;
            var isPremium = SubscriptionTierExtensions.IsPremiumClaim(tierClaim);
            
            // Premium users: 300 req/min, Standard/Trial: 200 req/min (~3.3/s — headroom for
            // rapid page navigation, where each page fires several parallel data calls at once).
            var permitLimit = isPremium ? 300 : 200;
            
            return RateLimitPartition.GetSlidingWindowLimiter(
                partitionKey: $"user:{userId}",
                factory: _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 4,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 10
                });
        }
        
        // Unauthenticated: Stricter IP-based limit (30 req/min)
        return RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: $"anon:{BuoyAI.API.RateLimitKeys.ClientIp(httpContext, edition.IsSelfHost)}",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 4,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 3
            });
    });
    
    // AI endpoints: Lower limits due to expensive API calls
    // Per-user only (must be authenticated to use AI)
    options.AddPolicy("ai", httpContext =>
    {
        var userId = httpContext.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        
        if (!string.IsNullOrEmpty(userId))
        {
            var tierClaim = httpContext.User?.FindFirst("subscription_tier")?.Value;
            var isPremium = SubscriptionTierExtensions.IsPremiumClaim(tierClaim);
            
            // Premium: 30 AI req/min, Standard/Trial: 15 AI req/min
            var permitLimit = isPremium ? 30 : 15;
            
            return RateLimitPartition.GetSlidingWindowLimiter(
                partitionKey: $"ai:{userId}",
                factory: _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 4,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 2
                });
        }
        
        // Unauthenticated AI requests - very strict (shouldn't happen, but safety)
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"ai-anon:{BuoyAI.API.RateLimitKeys.ClientIp(httpContext, edition.IsSelfHost)}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });
    });

    // BuoyAI Connect (MCP) endpoint: 30 tools/calls per minute, partitioned per user (the token's
    // nameid). Modeled on the premium "ai" tier — a single external agent shouldn't be able to hammer
    // the tool surface. Connect tokens are always authenticated, so there is no anonymous branch.
    options.AddPolicy("mcp", httpContext =>
    {
        var userId = httpContext.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? httpContext.User?.FindFirst("nameid")?.Value;

        return RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: string.IsNullOrEmpty(userId)
                ? $"mcp-anon:{BuoyAI.API.RateLimitKeys.ClientIp(httpContext, edition.IsSelfHost)}"
                : $"mcp:{userId}",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 4,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 2
            });
    });

    // Full data export: reads every table the caller owns, so it is heavy. 6 per 10 minutes per
    // user, no queueing (an over-limit request gets 429 right away). Every request counts, so an MFA
    // challenge (403) and its retry use two permits, and a gate rejection (429) uses one: 6 leaves
    // room for a challenge plus a few real downloads.
    options.AddPolicy("export", httpContext =>
    {
        var userId = httpContext.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? httpContext.User?.FindFirst("nameid")?.Value;

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: string.IsNullOrEmpty(userId)
                ? $"export-anon:{BuoyAI.API.RateLimitKeys.ClientIp(httpContext, edition.IsSelfHost)}"
                : $"export:{userId}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 6,
                Window = TimeSpan.FromMinutes(10),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });
    });

    // On rate limit exceeded, log and return custom response
    options.OnRejected = async (context, cancellationToken) =>
    {
        var userId = context.HttpContext.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        Log.Warning("Rate limit exceeded for {IP} (User: {UserId}) on {Path}",
            BuoyAI.API.RateLimitKeys.ClientIp(context.HttpContext, edition.IsSelfHost),
            userId ?? "anonymous",
            context.HttpContext.Request.Path);
        
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.ContentType = "application/json";
        
        // Emit BOTH `message` and `error` so every client error-parser surfaces the real
        // rate-limit text: most UIs read `data['message']`, while a few legacy sites read
        // `error`. Include `retryAfterSeconds` (and the Retry-After header when available) so
        // the client can tell the user when to retry.
        const string rateLimitMessage = "Too many requests. Please try again later.";
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
            await context.HttpContext.Response.WriteAsJsonAsync(new
            {
                message = rateLimitMessage,
                error = rateLimitMessage,
                retryAfterSeconds = (int)retryAfter.TotalSeconds
            }, cancellationToken);
        }
        else
        {
            await context.HttpContext.Response.WriteAsJsonAsync(new
            {
                message = rateLimitMessage,
                error = rateLimitMessage
            }, cancellationToken);
        }
    };
});

// Configure JWT Authentication with environment-specific validation
var jwtSettings = builder.Configuration.GetSection("Jwt");
var currentEnvironment = builder.Environment.EnvironmentName;

// Refuse to start with a missing, placeholder or too-short Jwt:Key / Encryption:Key / Mcp:SigningKey.
// The values in appsettings.Example.json are placeholders and must be replaced before first run.
StartupSecretsValidator.ValidateOrThrow(
  builder.Configuration,
  currentEnvironment,
  warning => Log.Warning("Startup config: {Warning}", warning));
var jwtKey = jwtSettings["Key"]!;
Log.Information("Startup secret validation passed for {Environment} environment", currentEnvironment);

var key = Encoding.ASCII.GetBytes(jwtKey);
// MCP/OAuth access tokens use their own signing key (Mcp:SigningKey). Without it they fall back to
// Jwt:Key so existing deployments keep working; the validator logs a warning in that case.
var mcpSigningKey = Encoding.ASCII.GetBytes(StartupSecretsValidator.ResolveMcpSigningKey(builder.Configuration));

var mcpEnabledForAuth = builder.Configuration.GetValue<bool>("Mcp:Enabled");
var authenticationBuilder = builder.Services.AddAuthentication(x =>
{
  // A path-routing policy scheme is the default so the framework's automatic per-request
  // authentication picks the RIGHT JWT scheme. Without it, UseAuthentication runs the
  // first-party scheme on every request — including /mcp, where a Connect token's audience
  // (Mcp:ResourceUri) never matches the first-party audience — logging a benign but noisy
  // IDX10214 warning on every MCP call (#782 review follow-up). Audience isolation is
  // unchanged: /mcp forwards to McpBearer, so a first-party token there still 401s.
  x.DefaultScheme = "SmartJwt";
  x.DefaultChallengeScheme = "SmartJwt";
})
.AddPolicyScheme("SmartJwt", "SmartJwt", options =>
{
  options.ForwardDefaultSelector = context =>
    mcpEnabledForAuth && context.Request.Path.StartsWithSegments("/mcp")
      ? BuoyAI.API.Endpoints.McpConnectEndpoints.BearerScheme
      : JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(x =>
{
  x.RequireHttpsMetadata = false; // Set to true in production
  x.SaveToken = true;
  x.TokenValidationParameters = new TokenValidationParameters
  {
    ValidateIssuerSigningKey = true,
    IssuerSigningKey = new SymmetricSecurityKey(key),
    ValidateIssuer = true,
    ValidIssuer = jwtSettings["Issuer"],
    ValidateAudience = true,
    ValidAudience = jwtSettings["Audience"],
    ValidateLifetime = true,
    ClockSkew = TimeSpan.Zero
  };
  
  // Handle claim mapping after token validation
  x.Events = new JwtBearerEvents
  {
    OnAuthenticationFailed = context =>
    {
      Log.Warning("JWT Authentication failed: {Error}",
        context.Exception?.Message ?? "Unknown error");
      return Task.CompletedTask;
    },
    OnTokenValidated = async context =>
    {
      var claimsIdentity = context.Principal?.Identity as ClaimsIdentity;
      if (claimsIdentity != null)
      {
        // Map 'nameid' claim to ClaimTypes.NameIdentifier
        var nameIdClaim = claimsIdentity.FindFirst("nameid");
        if (nameIdClaim != null && !claimsIdentity.HasClaim(ClaimTypes.NameIdentifier, nameIdClaim.Value))
        {
          claimsIdentity.AddClaim(new Claim(ClaimTypes.NameIdentifier, nameIdClaim.Value));
        }
        
        // Map 'unique_name' claim to ClaimTypes.Name
        var uniqueNameClaim = claimsIdentity.FindFirst("unique_name");
        if (uniqueNameClaim != null && !claimsIdentity.HasClaim(ClaimTypes.Name, uniqueNameClaim.Value))
        {
          claimsIdentity.AddClaim(new Claim(ClaimTypes.Name, uniqueNameClaim.Value));
        }
        
        // Map subscription_tier to Role claim for [Authorize(Roles = "Admin")] support
        var subscriptionTierClaim = claimsIdentity.FindFirst("subscription_tier");
        if (subscriptionTierClaim != null && subscriptionTierClaim.Value == "Admin")
        {
          claimsIdentity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
        }
      }

      // Signature/expiry alone is not enough: a token for a deleted (or seeder-recreated) user
      // must 401 so the client logs out, instead of every controller returning empty data.
      await BuoyAI.API.Authentication.JwtUserExistenceValidator.ValidateAsync(context);
    },
    OnChallenge = context =>
    {
      Log.Warning("JWT Challenge for {Path}: {Error} - {ErrorDescription}. Auth header present: {HasAuth}", 
        context.Request.Path,
        context.Error ?? "No error",
        context.ErrorDescription ?? "No description",
        context.Request.Headers.ContainsKey("Authorization"));
      return Task.CompletedTask;
    }
  };
});

// BuoyAI Connect (RFC #782): a SECOND JWT bearer scheme dedicated to Connect access tokens, wired
// only when the feature flag is on. It validates the MCP-pinned iss/aud (Mcp:Issuer / Mcp:ResourceUri)
// with the SAME symmetric signing key, so a first-party token (iss "BuoyAI", aud "BuoyAI-Users") can
// never satisfy it and vice-versa. Inbound claim mapping is disabled so nameid/scope/client_id reach
// the handler verbatim. On a 401 challenge it advertises the protected-resource metadata document per
// RFC 9728 so a compliant client can discover the authorization server.
if (builder.Configuration.GetValue<bool>("Mcp:Enabled"))
{
  var mcpIssuer = (builder.Configuration["Mcp:Issuer"] ?? string.Empty).TrimEnd('/');
  var mcpResource = builder.Configuration["Mcp:ResourceUri"] ?? string.Empty;
  var resourceMetadataUrl = $"{mcpIssuer}/.well-known/oauth-protected-resource/mcp";

  authenticationBuilder.AddJwtBearer(BuoyAI.API.Endpoints.McpConnectEndpoints.BearerScheme, options =>
  {
    options.RequireHttpsMetadata = false;
    options.MapInboundClaims = false; // keep nameid / scope / client_id as-is
    options.TokenValidationParameters = new TokenValidationParameters
    {
      ValidateIssuerSigningKey = true,
      IssuerSigningKey = new SymmetricSecurityKey(mcpSigningKey),
      ValidateIssuer = true,
      ValidIssuer = mcpIssuer,
      ValidateAudience = true,
      ValidAudience = mcpResource,
      ValidateLifetime = true,
      ClockSkew = TimeSpan.Zero
    };
    options.Events = new JwtBearerEvents
    {
      // A personal access token (self-host) is revocable: its jti must name an unrevoked row of this user's.
      OnTokenValidated = async context =>
      {
        var principal = context.Principal;
        if (principal?.FindFirst("client_id")?.Value != BuoyAI.Core.Interfaces.IConnectPersonalTokenService.ClientId)
          return;
        var jti = principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti)?.Value;
        var sub = principal.FindFirst("nameid")?.Value
          ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var tokens = context.HttpContext.RequestServices.GetRequiredService<BuoyAI.Core.Interfaces.IConnectPersonalTokenService>();
        if (!Guid.TryParse(jti, out var tokenId) || !int.TryParse(sub, out var userId)
            || !await tokens.ValidateAsync(tokenId, userId, context.HttpContext.RequestAborted))
          context.Fail("This access token was revoked or has expired.");
      },
      OnChallenge = context =>
      {
        // RFC 9728 §5.1: point unauthenticated clients at the protected-resource metadata.
        context.HttpContext.Response.Headers.Append(
          "WWW-Authenticate", $"Bearer resource_metadata=\"{resourceMetadataUrl}\"");
        return Task.CompletedTask;
      }
    };
  });

  // The /mcp endpoint accepts ONLY Connect tokens (the McpBearer scheme) — never a first-party JWT.
  builder.Services.AddAuthorization(authOptions =>
  {
    authOptions.AddPolicy(BuoyAI.API.Endpoints.McpConnectEndpoints.AuthPolicy, policy =>
    {
      policy.AuthenticationSchemes = new[] { BuoyAI.API.Endpoints.McpConnectEndpoints.BearerScheme };
      policy.RequireAuthenticatedUser();
    });
  });

  builder.Services.AddBuoyAiConnectMcp();
}

// Configure CORS
builder.Services.AddCors(options =>
{
  options.AddPolicy("AllowWebApp", policy =>
  {
    policy.AllowAnyHeader()
          .AllowAnyMethod()
          .AllowCredentials();
    if (builder.Environment.IsDevelopment())
    {
      // Dev / agent fleet: allow ANY loopback origin so every per-slot Flutter web
      // port (3000, 3100, 3200, …) can reach the API. SetIsOriginAllowed reflects
      // the specific origin, so AllowCredentials stays valid.
      policy.SetIsOriginAllowed(origin =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback);
    }
    else
    {
      policy.WithOrigins("http://localhost:3000", "http://localhost:5173"); // local web dev servers
    }
  });
});

// Configure Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
  c.SwaggerDoc("v1", new OpenApiInfo
  {
    Title = "BuoyAI API",
    Version = "v1",
    Description = "Personal Finance Management API with AI-powered insights"
  });

  // Add JWT authentication to Swagger
  c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
  {
    Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
    Name = "Authorization",
    In = ParameterLocation.Header,
    Type = SecuritySchemeType.ApiKey,
    Scheme = "Bearer"
  });

  c.AddSecurityRequirement(document => new OpenApiSecurityRequirement
  {
    [new OpenApiSecuritySchemeReference("Bearer", document)] = []
  });
});

var app = builder.Build();

// MUST be the first middleware: behind a TLS-terminating reverse proxy, honor X-Forwarded-Proto /
// X-Forwarded-For so Request.IsHttps and Request.Scheme reflect the ORIGINAL (HTTPS) request.
// Without this, IsHttps is always false behind the proxy, so `Secure = Request.IsHttps` never sets
// the Secure flag on the refresh-token cookie, and client IP/scheme are wrong everywhere.
//
// Trust settings (all optional):
//   ForwardedHeaders:Enabled          default true. false = ignore X-Forwarded-* entirely (API exposed directly).
//   ForwardedHeaders:TrustedProxies   list of proxy IP addresses allowed to set the headers.
//   ForwardedHeaders:TrustedNetworks  list of CIDR ranges (for example 172.16.0.0/12) allowed to set them.
//   ForwardedHeaders:ForwardLimit     number of proxy hops to read; default is the framework's 1.
// With neither list set we trust any sender, which is what the hosted two-hop nginx chain
// (host nginx -> container nginx -> API) has always relied on. Self-hosters should set a list.
if (app.Configuration.GetValue("ForwardedHeaders:Enabled", true))
{
  var forwardedHeadersOptions = new ForwardedHeadersOptions
  {
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
  };
  var forwardLimit = app.Configuration.GetValue<int?>("ForwardedHeaders:ForwardLimit");
  if (forwardLimit.HasValue) forwardedHeadersOptions.ForwardLimit = forwardLimit.Value;

  var trustedProxies = app.Configuration.GetSection("ForwardedHeaders:TrustedProxies").Get<string[]>() ?? Array.Empty<string>();
  var trustedNetworks = app.Configuration.GetSection("ForwardedHeaders:TrustedNetworks").Get<string[]>() ?? Array.Empty<string>();
  forwardedHeadersOptions.KnownIPNetworks.Clear();
  forwardedHeadersOptions.KnownProxies.Clear();
  if (trustedProxies.Length == 0 && trustedNetworks.Length == 0)
  {
    Log.Information("ForwardedHeaders: no TrustedProxies/TrustedNetworks set; trusting X-Forwarded-* from any sender");
  }
  else
  {
    foreach (var proxy in trustedProxies)
      forwardedHeadersOptions.KnownProxies.Add(System.Net.IPAddress.Parse(proxy.Trim()));
    foreach (var network in trustedNetworks)
    {
      var parts = network.Trim().Split('/');
      forwardedHeadersOptions.KnownIPNetworks.Add(
        new System.Net.IPNetwork(System.Net.IPAddress.Parse(parts[0]), int.Parse(parts[1])));
    }
    Log.Information("ForwardedHeaders: trusting {Proxies} proxies and {Networks} networks", trustedProxies.Length, trustedNetworks.Length);
  }
  app.UseForwardedHeaders(forwardedHeadersOptions);
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
  app.UseSwagger();
  app.UseSwaggerUI(c =>
  {
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "BuoyAI API v1");
    c.RoutePrefix = "swagger";
  });
}

// Ensure database is created and seeded
using (var scope = app.Services.CreateScope())
{
  var dbContext = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
  var environment = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
  // Real ILogger so the migration runner's structured logs (incl. the LogCritical on a failed
  // migration) reach Serilog/Sentry instead of being silently dropped on a null logger.
  var migrationLogger = scope.ServiceProvider
    .GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
    .CreateLogger("BuoyAI.Database.Migrations");

  try
  {
    // Migrations:BackupBeforeMigrate (default false) turns on the verified pre-migration pg_dump.
    var migrationSettings = app.Configuration.GetSection("Migrations").Get<MigrationSettings>();
    await DataSeeder.SeedAsync(dbContext, environment, migrationLogger, migrationSettings, seedData: !edition.IsSelfHost);
    Log.Information("Database initialized and seeded successfully");
  }
  catch (Exception ex)
  {
    Log.Error(ex, "Error initializing database");
    
    // In production/staging, fail loudly if migrations fail - don't run with inconsistent DB
    if (!environment.IsDevelopment())
    {
      Log.Fatal("Database migration failed in {Environment}. Application cannot start safely.", environment.EnvironmentName);
      throw;
    }
    // In development, log warning but allow app to continue (for debugging)
    Log.Warning("Database migration failed in Development. App will continue but may have issues.");
  }
}

app.UseMiddleware<ErrorHandlingMiddleware>();

// Add headers to support OAuth popup communication
app.Use(async (context, next) =>
{
  // Set COOP header that allows OAuth popups
  context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin-allow-popups";
  
  await next();
});

// Off by default: the app normally sits behind a TLS-terminating proxy (hosted) or on a plain LAN
// behind the operator's own proxy (self-host), and it listens on HTTP only. Turn it on with
// Https:RedirectEnabled=true only when Kestrel itself serves HTTPS (and set ASPNETCORE_HTTPS_PORT).
if (app.Configuration.GetValue("Https:RedirectEnabled", false))
{
  app.UseHttpsRedirection();
}

// Self-host only: Kestrel serves the Flutter web build under /app/ (hosted uses nginx for this).
string? selfHostWebRoot = null;
if (edition.IsSelfHost)
{
  selfHostWebRoot = SelfHostWebApp.ResolveWebRoot(app.Configuration);
  if (selfHostWebRoot != null)
  {
    app.UseSelfHostWebApp(selfHostWebRoot);
    Log.Information("Serving the web app from {WebRoot} at /app/", selfHostWebRoot);
  }
  else
  {
    Log.Warning("Self-host edition, but no web build found (SelfHost:WebRoot or {Default}). The API runs; the web app is not served.",
      Path.Combine(AppContext.BaseDirectory, "web"));
  }
}

app.UseCors("AllowWebApp");

// Enable response caching for endpoints with [ResponseCache] attribute
app.UseResponseCaching();

// Self-host: the hosted-only routes (billing, Plaid, referrals, ...) answer 404 for everyone,
// including signed-out callers. Needs routing to have run (endpoint metadata) and must come
// before authentication, which would otherwise answer 401 and reveal the route exists.
app.UseHostedOnlyGate(edition);
app.UseSelfHostOnlyGate(edition);

app.UseAuthentication();
app.UseAuthorization();

// Rate limiting MUST run after authentication so authenticated requests are partitioned
// per-user ("user:{id}", 120–300 req/min) instead of being seen as anonymous. Behind the
// nginx reverse proxy the connection IP is the proxy (127.0.0.1), so with the limiter ordered
// before auth EVERY user collapsed into one shared "anon:127.0.0.1" bucket (30 req/min) — the
// app's parallel page load (accounts+txns+savings+notifications+subscriptions at once) hit that
// instantly and 429'd, so nothing loaded and the app felt slow. See prod logs
// ("Rate limit exceeded for 127.0.0.1 (User: anonymous)").
app.UseRateLimiter();

app.MapControllers();
if (selfHostWebRoot != null)
{
  app.MapSelfHostWebApp(selfHostWebRoot);
}

// BuoyAI Connect (RFC #782): only when the feature flag is on, expose the OAuth discovery documents
// at /.well-known/* and idempotently register the config-declared pre-registered clients (Claude,
// ChatGPT, local e2e) into oauth_clients. The OAuth controller itself is gated by [RequireMcpEnabled].
if (app.Configuration.GetValue<bool>("Mcp:Enabled"))
{
  app.MapOAuthDiscovery();

  // The public MCP endpoint. Stateless HTTP transport; requires a Connect token (McpBearer scheme
  // only) and its own 30/min-per-user rate limit. All tool exposure/authz lives in the handlers.
  app.MapMcp("/mcp")
    .RequireAuthorization(BuoyAI.API.Endpoints.McpConnectEndpoints.AuthPolicy)
    .RequireRateLimiting(BuoyAI.API.Endpoints.McpConnectEndpoints.RateLimitPolicy);

  using (var connectScope = app.Services.CreateScope())
  {
    try
    {
      var connectService = connectScope.ServiceProvider.GetRequiredService<IOAuthConnectService>();
      await connectService.UpsertConfiguredClientsAsync(CancellationToken.None);
      Log.Information("BuoyAI Connect enabled: OAuth discovery mapped and configured clients upserted.");
    }
    catch (Exception ex)
    {
      Log.Error(ex, "Failed to upsert configured BuoyAI Connect clients at startup.");
    }
  }
}

// Lightweight version probe so a running container can report which build it is
// (the deploy/artifact version — one lockstep SemVer for the product; see
// VERSIONING.md). Anonymous. The version flows from backend/Directory.Build.props.
app.MapGet("/api/version", () => Results.Ok(new { version = AppVersion.Current })).AllowAnonymous();

// Map Aspire default endpoints (health checks at /health and /alive)
app.MapDefaultEndpoints();

app.Run();

// Make Program class accessible for integration tests
public partial class Program { }
