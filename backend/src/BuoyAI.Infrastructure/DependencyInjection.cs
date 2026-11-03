using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Resend;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Interfaces.Repositories;
using BuoyAI.Core.Interfaces.Services;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.MCP;
using BuoyAI.Infrastructure.MCP.Tools;
using BuoyAI.Infrastructure.Repositories;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Services.SimpleFin;
using BuoyAI.Infrastructure.Services.Bridge;
using BuoyAI.Infrastructure.Services.Ai;
using BuoyAI.Infrastructure.Telemetry;

namespace BuoyAI.Infrastructure;

public static class DependencyInjection
{
  /// <summary>
  /// Cap the Npgsql connection pool. Prod and staging run as separate processes on the
  /// same single VPS, and Npgsql defaults each pool to 100, so two of those would exceed
  /// PostgreSQL's max_connections (100) under load. Only applied when the connection
  /// string doesn't already specify a pool size; override via "Database:MaxPoolSize".
  /// </summary>
  internal static string? CapPoolSize(string? connectionString, IConfiguration configuration)
  {
    if (string.IsNullOrEmpty(connectionString)
        || connectionString.Contains("Pool Size", StringComparison.OrdinalIgnoreCase))
      return connectionString;
    return new NpgsqlConnectionStringBuilder(connectionString)
    {
      MaxPoolSize = configuration.GetValue<int?>("Database:MaxPoolSize") ?? 40
    }.ConnectionString;
  }

  /// <summary>
  /// Picks the AI provider once, from <c>Ai:Provider</c> (hosted default gemini, self-host default
  /// none). The provider is a singleton for the life of the process; changing it means a restart.
  /// </summary>
  private static void AddAiProvider(IServiceCollection services, IConfiguration configuration, EditionInfo edition)
  {
    var options = AiProviderOptions.From(configuration, edition);
    services.AddSingleton(options);
    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
    if (options.IsBuoyCloud)
      services.AddSingleton(new CloudAiGatewayClient(http, options));
    services.AddSingleton<IAiProvider>(sp =>
    {
      IAiProvider inner;
      if (options.IsGemini) inner = ActivatorUtilities.CreateInstance<GeminiNativeProvider>(sp);
      else if (options.IsBuoyCloud) inner = new CloudAiProvider(options, sp.GetRequiredService<CloudAiGatewayClient>());
      else if (options.IsOpenAiCompatible)
        inner = new OpenAiCompatibleProvider(options, http,
          sp.GetRequiredService<ILogger<OpenAiCompatibleProvider>>(), sp.GetService<BuoyAiMetrics>());
      else return new NullAiProvider();

      // Every call a user causes writes a usage row with the provider's real token counts (see AiUsageScope).
      return new MeteredAiProvider(inner, sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<ILogger<MeteredAiProvider>>());
    });
    // One embedding client (model, dimension, REST call) shared by the three vector indexes.
    // Follows the provider: Gemini on hosted, an OpenAI-compatible server when it has an
    // embedding model, otherwise off.
    services.AddSingleton<IEmbeddingClient>(sp =>
    {
      if (options.IsGemini) return ActivatorUtilities.CreateInstance<GeminiEmbeddingClient>(sp);
      if (options.IsOpenAiCompatible) return new OpenAiCompatibleEmbeddingClient(options, http);
      return new DisabledEmbeddingClient();
    });
  }

  public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, bool skipDbContext = false)
  {
    // Register Lazy<T> factory for breaking circular dependencies
    services.AddTransient(typeof(Lazy<>), typeof(LazilyResolved<>));

    // One edition answer for the whole container. Hosted (the default) registers everything as it
    // always has; selfhost leaves out billing, Plaid-direct and the growth jobs. See EditionInfo.
    var edition = EditionInfo.From(configuration);
    services.TryAddSingleton(edition);
    
    // Database - supports both Aspire-injected "BuoyAI" and legacy "DefaultConnection"
    // Skip DbContext registration when:
    // 1. skipDbContext parameter is true
    // 2. Environment is "Testing" (for integration tests that use InMemory database)
    var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
    if (!skipDbContext && environment != "Testing")
    {
      var connectionString = configuration.GetConnectionString("BuoyAI")
                          ?? configuration.GetConnectionString("DefaultConnection");

      connectionString = CapPoolSize(connectionString, configuration);

      void ConfigureDbOptions(DbContextOptionsBuilder options) =>
        options
          .UseNpgsql(connectionString, npgsqlOptions =>
          {
            // Enable automatic retry on transient failures (connection drops, timeouts)
            npgsqlOptions.EnableRetryOnFailure(
              maxRetryCount: 5,                              // Retry up to 5 times
              maxRetryDelay: TimeSpan.FromSeconds(30),       // Max delay between retries
              errorCodesToAdd: null);                        // Use default PostgreSQL error codes

            // Set command timeout for long-running queries
            npgsqlOptions.CommandTimeout(30);
          })
          // EF Core 10+ throws PendingModelChangesWarning by default when model changes
          // exist that aren't captured in migrations. We log instead of throw to allow
          // migrations to apply even when the model snapshot may be slightly out of sync.
          .ConfigureWarnings(warnings => warnings
            .Log(RelationalEventId.PendingModelChangesWarning));

      services.AddDbContext<BuoyAIDbContext>(ConfigureDbOptions);

      // Also expose an IDbContextFactory so services that run at the tail of a request
      // pipeline (e.g. RefreshTokenService, issuing a token after device-trust writes)
      // can obtain a short-lived context of their own instead of racing the request-scoped
      // one. Registered Scoped (not the default Singleton) because AddDbContext registered
      // DbContextOptions<BuoyAIDbContext> as scoped, and a singleton factory cannot consume
      // a scoped options ("Cannot consume scoped service … from singleton"). A scoped
      // factory still hands each CreateDbContext call a brand-new, independent context.
      services.AddDbContextFactory<BuoyAIDbContext>(ConfigureDbOptions, ServiceLifetime.Scoped);
    }

    // Repositories
    services.AddScoped<IUserRepository, UserRepository>();
    services.AddScoped<IAccountRepository, AccountRepository>();
    services.AddScoped<ITransactionRepository, TransactionRepository>();
    services.AddScoped<IBudgetRepository, BudgetRepository>();
    services.AddScoped<IPlaidConnectionRepository, PlaidConnectionRepository>();
    services.AddScoped<ISubscriptionRepository, SubscriptionRepository>();
    services.AddScoped<ISubscriptionTransactionRepository, SubscriptionTransactionRepository>();
    services.AddScoped<IDismissedSubscriptionPatternRepository, DismissedSubscriptionPatternRepository>();
    services.AddScoped<ISuggestedSubscriptionRepository, SuggestedSubscriptionRepository>();
    services.AddScoped<ICrowdsourcedSubscriptionPatternRepository, CrowdsourcedSubscriptionPatternRepository>();
    services.AddScoped<ISavingsRepository, SavingsRepository>();
    services.AddScoped<IAIInsightRepository, AIInsightRepository>();
    services.AddScoped<IUserConsentRepository, UserConsentRepository>();
    services.AddScoped<IOnboardingQuizRepository, OnboardingQuizRepository>();
    services.AddScoped<IFamilyRepository, FamilyRepository>();
    services.AddScoped<IFamilyMemberRepository, FamilyMemberRepository>();
    services.AddScoped<IFamilyInvitationRepository, FamilyInvitationRepository>();
    services.AddScoped<ICategoryGroupRepository, CategoryGroupRepository>();
    services.AddScoped<ICategoryRepository, CategoryRepository>();

    // Configuration
    services.AddSingleton<AIModelConfiguration>();
    
    // Telemetry / Custom Metrics
    services.AddSingleton<BuoyAiMetrics>();
    // Shared Gemini spend/refusal reporting for every service that calls the API.
    services.AddSingleton<GeminiUsageReporter>();
    
    // Circuit Breaker - Singleton for shared state across requests
    services.AddSingleton<ICircuitBreakerService, CircuitBreakerService>();

    // Services
    // Entitlements (effective tier, Premium access, feature gates, AI caps) never depend on
    // Stripe. The hosted build adds the Stripe re-check as an optional hook. Self-host has no
    // billing at all: nothing below registers Stripe, so StripeService (whose constructor throws
    // without keys) cannot be reached, and every account gets the full feature set.
    if (edition.IsHosted)
    {
      services.AddScoped<IStripeService, StripeService>();
      services.AddScoped<IEntitlementService, EntitlementService>();
      services.AddScoped<ITierRefreshHook, StripeTierRefreshHook>();
      services.AddScoped<IBillingService, BillingService>();
    }
    else
    {
      services.AddScoped<IEntitlementService, SelfHostEntitlementService>();
      // Sign-up lock: first account is the admin, then closed unless opened or invited (task 09).
      services.AddSingleton<FirstAccountLock>();
      services.AddScoped<RegistrationGate>();
    }
    services.AddScoped<IAuthService, AuthService>();
    services.AddScoped<IGoogleTokenValidator, GoogleTokenValidator>();
    // Singleton so Apple's fetched JWKS signing keys are cached across requests.
    services.AddSingleton<IAppleTokenValidator, AppleTokenValidator>();
    // Signs the Apple client secret (.p8) and calls Apple's token/revoke endpoints.
    services.AddSingleton<IAppleAuthClient, AppleAuthClient>();
    services.AddScoped<IRefreshTokenService, RefreshTokenService>();

    // Single sign-on with the operator's OpenID Connect provider (self-host; the endpoints answer
    // 404 on hosted). Singleton client so discovery and signing keys are cached.
    services.AddHttpClient(BuoyAI.Infrastructure.Services.Oidc.OidcProviderClient.HttpClientName,
      c => c.Timeout = TimeSpan.FromSeconds(15));
    services.AddSingleton<BuoyAI.Infrastructure.Services.Oidc.OidcProviderClient>();
    services.AddScoped<BuoyAI.Infrastructure.Services.Oidc.OidcLoginService>();

    // BuoyAI Connect — OAuth 2.1 authorization server (RFC #782). Bind the Mcp options and register
    // the core service. The CIMD fetch uses a dedicated HttpClient with auto-redirect DISABLED so a
    // 3xx from a metadata document (to any host) is never followed.
    services.Configure<BuoyAI.Core.Configuration.McpOptions>(
      configuration.GetSection(BuoyAI.Core.Configuration.McpOptions.SectionName));
    // SocketsHttpHandler (not HttpClientHandler) so we can attach a ConnectCallback: it re-validates
    // the IP the client is ABOUT to dial and connects only to that address. This closes the DNS-rebind
    // window between the service's pre-fetch DNS check and the actual connect — the address classified
    // here is the exact one the socket uses. AllowAutoRedirect stays off so a 3xx is never followed.
    services.AddHttpClient("oauth-cimd")
      .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
      {
        AllowAutoRedirect = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
          var host = context.DnsEndPoint.Host;
          IPAddress[] candidates = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host, cancellationToken);

          // Only dial addresses that pass the same public-IP classifier used pre-fetch.
          var publicAddresses = candidates.Where(OAuthConnectService.IsPublicIpAddress).ToArray();
          if (publicAddresses.Length == 0)
            throw new HttpRequestException("SSRF guard: CIMD host does not resolve to a public address.");

          var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
          try
          {
            await socket.ConnectAsync(publicAddresses, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
          }
          catch
          {
            socket.Dispose();
            throw;
          }
        },
      });
    services.AddScoped<IOAuthConnectService, OAuthConnectService>();
    services.AddScoped<IConnectPersonalTokenService, ConnectPersonalTokenService>();

    services.AddScoped<IOnboardingQuizService, OnboardingQuizService>();
    // PlaidService backs both the Plaid-specific IPlaidService and the provider-neutral
    // IBankAggregator seam (#310) — register the concrete once and alias both to the same scoped
    // instance. A new provider adds one line: services.AddScoped<IBankAggregator, MxAggregator>().
    // Self-host talks to no Plaid account of its own, so nothing here is registered and anything
    // that wants Plaid resolves it on use (Lazy) and skips when it is absent.
    if (edition.IsHosted)
    {
      services.AddScoped<PlaidService>();
      services.AddScoped<IPlaidService>(sp => sp.GetRequiredService<PlaidService>());
      services.AddScoped<IBankAggregator>(sp => sp.GetRequiredService<PlaidService>());
      services.AddScoped<IPlaidUsageTracker, PlaidUsageTracker>();
    }
    // SimpleFIN, the second provider: offered in both editions, but only where the SimpleFin
    // settings turn it on (self-host: on unless disabled; hosted: off unless enabled), so hosted
    // behaves as it did until someone asks for it.
    var simpleFin = configuration.GetSection(SimpleFinOptions.SectionName).Get<SimpleFinOptions>() ?? new SimpleFinOptions();
    var simpleFinEnabled = simpleFin.Enabled ?? edition.IsSelfHost;
    services.Configure<SimpleFinOptions>(configuration.GetSection(SimpleFinOptions.SectionName));
    // The client and aggregator are always built, so a connection made while SimpleFIN was on can
    // still be disconnected (and its stored credential wiped) after it is switched off. Only the
    // IBankAggregator registration, which decides what is offered, connected and synced, is gated.
    // A pasted token chooses the URL we call, so the connection is made only to public addresses
    // and a redirect is never followed (the same guard the OAuth metadata fetch uses).
    services.AddHttpClient(SimpleFinClient.HttpClientName, c =>
      {
        c.Timeout = TimeSpan.FromSeconds(Math.Max(5, simpleFin.RequestTimeoutSeconds));
        c.MaxResponseContentBufferSize = 20 * 1024 * 1024; // a year of one household's transactions is far smaller
      })
      .ConfigurePrimaryHttpMessageHandler(PublicOnlyHttpHandler.Create);
    services.AddScoped<SimpleFinClient>();
    services.AddScoped<SimpleFinAggregator>();
    if (simpleFinEnabled)
    {
      services.AddScoped<IBankAggregator>(sp => sp.GetRequiredService<SimpleFinAggregator>());
    }
    else
    {
      services.AddScoped(sp => new LifecycleOnlyAggregator(sp.GetRequiredService<SimpleFinAggregator>()));
    }
    // BuoyAI Cloud bank feed (Buoy Money task 14): a third provider for self-hosted servers. The pairing service is always
    // there so the admin screen can say "not set up"; the client and the aggregator exist only where CloudBridge:Url is set,
    // so hosted behaves as it did until someone asks for it. The aggregator also needs CloudBridge:BankFeedEnabled.
    var cloudBridge = configuration.GetSection(CloudBridgeOptions.SectionName).Get<CloudBridgeOptions>() ?? new CloudBridgeOptions();
    services.Configure<CloudBridgeOptions>(configuration.GetSection(CloudBridgeOptions.SectionName));
    services.TryAddSingleton(TimeProvider.System);
    services.AddHttpClient(CloudPairingService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(Math.Max(5, cloudBridge.RequestTimeoutSeconds)));
    services.AddSingleton<CloudSecretProtector>();
    services.AddScoped<CloudPairingService>();
    services.AddScoped<ICloudPairingService>(sp => sp.GetRequiredService<CloudPairingService>());
    services.AddSingleton<CloudLinkStateStore>();
    // The code link ("Link with BuoyAI Cloud"): one in-memory session and its poll loop. Self-host only (the controller is).
    services.AddSingleton<CloudDeviceLinkService>();
    if (!string.IsNullOrWhiteSpace(cloudBridge.Url))
    {
      services.AddScoped<BridgeClient>();
      // Pairing (the free push relay) needs only the address; the bank provider needs the owner to turn the feed on.
      if (cloudBridge.BankFeedEnabled)
      {
        services.AddSingleton<BridgePendingLinks>();
        services.AddScoped<IBankAggregator, BridgeAggregator>();
      }
    }
    services.AddScoped<IBankAggregatorRegistry, BankAggregatorRegistry>();
    services.AddScoped<IBankConnectionAlertService, BankConnectionAlertService>();
    services.AddScoped<IBankConnectionService, BankConnectionService>();
    // Account and connection reads/edits for the Accounts page: no bank provider involved, so both editions.
    services.AddScoped<IBankAccountService, BankAccountService>();
    services.AddScoped<ILastSyncTracker, LastSyncTracker>();
    services.AddScoped<IBudgetTransferService, BudgetTransferService>();
    services.AddScoped<IBudgetCategoryGroupService, BudgetCategoryGroupService>();
    services.AddScoped<IBudgetSpendingService, BudgetSpendingService>();
    services.AddScoped<IBudgetService, BudgetService>();
    services.AddScoped<IBudgetPeriodService, BudgetPeriodService>();
    services.AddScoped<ICountUpBudgetService, CountUpBudgetService>();
    services.AddScoped<ITransactionService, TransactionService>();
    services.AddScoped<ITransferDetectionService, TransferDetectionService>();
    services.AddScoped<IImportService, ImportService>();
    services.AddScoped<ITransactionImportService, TransactionImportService>();
    AddAiProvider(services, configuration, edition);
    services.AddScoped<IGeminiApiClient, GeminiApiClient>();
    services.AddScoped<IGeminiAgenticRunner, GeminiAgenticRunner>();
    services.AddScoped<IInsightActionService, InsightActionService>();
    services.AddScoped<IAIInsightGenerationService, AIInsightGenerationService>();
    services.AddScoped<IAIService, AIService>();
    services.AddScoped<IAICategorizationService, AICategorizationService>();
    // Ingest-time categorization policy (rules > trusted Plaid > AI queue) shared by every
    // sync/import path, plus the per-pass worker the queue drainer runs.
    services.AddScoped<ITransactionIngestCategorizer, TransactionIngestCategorizationService>();
    // Provider-neutral ingest (dedupe, pending-to-posted merge, create, categorize) fed by each bank adapter.
    services.AddScoped<ITransactionIngestService, TransactionIngestService>();
    services.AddScoped<AICategorizationQueueWorker>();
    services.AddScoped<IAIUsageTracker, AIUsageTracker>();
    services.AddScoped<IAiUsageSummaryService, AiUsageSummaryService>();
    services.AddScoped<IAIRefreshTracker, AIRefreshTracker>();
    services.AddScoped<ICostTrackingService, CostTrackingService>();
    services.AddScoped<IEncryptionService, EncryptionService>();
    services.AddScoped<ISubscriptionService, SubscriptionService>();
    services.AddScoped<ISavingsService, SavingsService>();
    services.AddScoped<IFamilyService, FamilyService>();
    services.AddScoped<IFamilyAuthorizationService, FamilyAuthorizationService>();
    services.AddScoped<IGamificationService, GamificationService>();
    if (edition.IsHosted) services.AddScoped<IReferralService, ReferralService>();
    services.AddScoped<IVisualizationService, VisualizationService>();
    services.AddScoped<ICategoryService, CategoryService>();
    
    // Update Mode Services
    if (edition.IsHosted)
    {
      services.AddScoped<IPlaidWebhookService, PlaidWebhookService>();
      services.AddScoped<IBankRequestService, BankRequestService>(); // "can't find my bank" -> support inbox
    }
    services.AddScoped<IConsentService, ConsentService>();
    services.AddScoped<IUserDataExportService, UserDataExportService>();
    services.AddScoped<INotificationService, NotificationService>();
    services.AddScoped<IFirstBudgetNudgeService, FirstBudgetNudgeService>();
    services.AddScoped<IWrappedService, WrappedService>(); // Money Wrapped recaps
    services.AddScoped<IWrappedGarnishService, WrappedGarnishService>(); // Wrapped v2 AI bonus cards
    services.AddScoped<INotificationRepository, NotificationRepository>();

    // Push notifications (#190): FCM sender is a singleton (one Firebase app per process,
    // no-op when credentials are absent); the pipeline service is scoped (uses DbContext).
    services.AddSingleton<IFcmSender, FcmSender>();
    // Relay push for self-hosted servers (task 09). Off unless PushRelay:Url is set.
    services.AddSingleton<IPushRelaySender, PushRelaySender>();
    services.AddScoped<IPushNotificationService, PushNotificationService>();

    // Offline-first sync batch dispatcher (#493)
    services.AddScoped<ISyncService, SyncService>();
    services.AddScoped<IEmailPreferencesService, EmailPreferencesService>();

    // Data Retention Services
    services.AddScoped<IDataRetentionManager, DataRetentionManager>();
    services.AddScoped<IDataRetentionAuditService, DataRetentionAuditService>();

    // Email and Two-Factor Authentication Services
    services.Configure<ResendClientOptions>(options =>
    {
        options.ApiToken = configuration["Resend:ApiKey"] ?? "test-api-key-for-development";
    });
    
    // WebAuthn Configuration
    services.Configure<WebAuthnSettings>(configuration.GetSection("WebAuthn"));
    
    // Stripe Configuration
    if (edition.IsHosted)
    {
      Stripe.StripeConfiguration.ApiKey = configuration["Stripe:SecretKey"] ?? "sk_test_..."; // Default test key for development
    }
    
    services.AddHttpClient();
    services.AddScoped<IResend, ResendClient>();
    // The one place email leaves the app. Hosted: Resend. Self-host: any SMTP server (Smtp:Host),
    // or nothing sent when none is set (see EmailTransports.cs).
    if (edition.IsSelfHost)
    {
      if (SmtpEmailTransport.IsSmtpConfigured(configuration)) services.AddSingleton<IEmailTransport, SmtpEmailTransport>();
      else services.AddSingleton<IEmailTransport, NoEmailTransport>();
    }
    else
    {
      services.AddScoped<IEmailTransport, ResendEmailTransport>();
    }

    // Receipt scanning
    services.AddSingleton(Services.Receipts.ReceiptOptions.From(configuration));
    services.AddScoped<Services.Receipts.IReceiptParser, Services.Receipts.ReceiptParser>();
    services.AddScoped<Services.Receipts.ReceiptService>();
    services.AddScoped<IReceiptService>(sp => sp.GetRequiredService<Services.Receipts.ReceiptService>());
    services.AddScoped<IEmailService, ResendEmailService>();

    // Marketing-site lead capture (email traded for an extended trial).
    services.AddScoped<IMarketingLeadService, MarketingLeadService>();

    // Founding-member $1 reservations. Off unless Reservations:Enabled is true.
    services.Configure<BuoyAI.Core.Configuration.ReservationOptions>(
      configuration.GetSection(BuoyAI.Core.Configuration.ReservationOptions.SectionName));
    services.Configure<BuoyAI.Core.Configuration.FoundingOptions>(
      configuration.GetSection(BuoyAI.Core.Configuration.FoundingOptions.SectionName));
    if (edition.IsHosted)
    {
      services.AddScoped<IReservationCheckoutClient, StripeReservationCheckoutClient>();
      services.AddScoped<IReservationService, ReservationService>();
      // Self-host closed beta invites for founding members. Off unless Beta:Enabled is true.
      services.Configure<BuoyAI.Core.Configuration.BetaOptions>(
        configuration.GetSection(BuoyAI.Core.Configuration.BetaOptions.SectionName));
      services.AddScoped<IBetaInviteService, BetaInviteService>();

      // Stripe-backed cardless trials: Stripe owns the trial lifecycle, we cache it locally.
      services.AddScoped<ITrialProvisioningService, TrialProvisioningService>();
      // THE single path by which Stripe subscription state reaches our DB.
      services.AddScoped<ISubscriptionSyncService, SubscriptionSyncService>();
      // BuoyAI Cloud plans: the app talks to the private Cloud billing service over HTTP only.
      // Off unless CloudBilling:Enabled is true; nothing leaves the server until then.
      services.Configure<BuoyAI.Core.Configuration.CloudBillingOptions>(
        configuration.GetSection(BuoyAI.Core.Configuration.CloudBillingOptions.SectionName));
      services.AddHttpClient(CloudBillingHttpClient.HttpClientName);
      services.AddScoped<ICloudBillingClient, CloudBillingHttpClient>();
      services.AddScoped<ICloudBillingService, CloudBillingService>();
      // BuoyAI Cloud portal: the user's self-hosted servers, through the Cloud pairing API with an owner token
      // this app signs. Off (404) unless CloudPortal:OwnerJwtKey is set to the bridge's Owner:JwtKey.
      services.Configure<BuoyAI.Core.Configuration.CloudPortalOptions>(
        configuration.GetSection(BuoyAI.Core.Configuration.CloudPortalOptions.SectionName));
      services.AddHttpClient(BuoyAI.Infrastructure.Services.CloudPortal.CloudPairingPortalClient.HttpClientName);
      services.AddSingleton<BuoyAI.Infrastructure.Services.CloudPortal.CloudOwnerTokenIssuer>();
      services.AddScoped<ICloudPairingPortal, BuoyAI.Infrastructure.Services.CloudPortal.CloudPairingPortalClient>();
      // Signed events from the Cloud services (AI allowance alerts, bridge notices). Off unless CloudEvents:WebhookSecret is set.
      services.Configure<BuoyAI.Core.Configuration.CloudEventsOptions>(
        configuration.GetSection(BuoyAI.Core.Configuration.CloudEventsOptions.SectionName));
      services.AddScoped<ICloudEventService, CloudEventService>();
      // In-app purchases: RevenueCat webhook + subscriber lookup (App Store now, Play later).
      services.AddHttpClient(RevenueCatSyncService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
      services.AddScoped<IRevenueCatSyncService, RevenueCatSyncService>();

      // Admin broadcast / incident email (#552)
      services.Configure<BroadcastSettings>(configuration.GetSection("Broadcast"));
      services.AddScoped<IBroadcastService, BroadcastService>();
    }
    services.AddScoped<ITwoFactorService, TwoFactorService>();
    services.AddScoped<IDeviceTrustService, DeviceTrustService>();
    services.AddScoped<IStepUpAuthService, StepUpAuthService>();
    // Hosted resolves login IPs to a city offline from the DB-IP City Lite database baked into the
    // image, so no IP leaves the server. Self-host has no database and returns "no location".
    if (edition.IsSelfHost) services.AddScoped<IIpGeolocationService, NoIpGeolocationService>();
    else
    {
      services.AddSingleton<LocalIpGeolocationService>();
      services.AddSingleton<IIpGeolocationService>(sp => sp.GetRequiredService<LocalIpGeolocationService>());
      services.AddHostedService<GeoDatabaseStartupCheck>();
    }
    services.AddScoped<IMfaPreferencesService, MfaPreferencesService>();
    services.AddScoped<IUserCategoryService, UserCategoryService>();
    services.AddScoped<ICategorizationRuleService, CategorizationRuleService>();

    // MCP (Model Context Protocol) Tools and Services
    services.AddScoped<FinancialReadTools>();
    services.AddScoped<BudgetReadTools>();
    services.AddScoped<CashFlowReadTools>();
    services.AddScoped<SavingsReadTools>();
    services.AddScoped<SubscriptionReadTools>();
    services.AddScoped<IncomeReadTools>();
    services.AddScoped<UserPreferencesTools>();
    services.AddScoped<UserContextReadTools>();
    services.AddScoped<GamificationReadTools>();
    services.AddScoped<HelpReadTools>();
    services.AddScoped<BudgetWriteTools>();
    services.AddScoped<SavingsWriteTools>();
    services.AddScoped<TransactionWriteTools>();
    services.AddSingleton<IMcpToolExecutor, McpToolExecutor>();
    // Intent-based tool filtering for chat (#740); flag AI:ToolFiltering:Enabled, fail-open.
    services.AddSingleton<IChatToolRouter, ChatToolRouter>();
    // Explicit prompt caching for long chats (#743); flag AI:PromptCaching:Enabled, fail-open.
    services.AddSingleton<IGeminiCacheClient, GeminiCacheClient>();
    services.AddSingleton<IChatPromptCache, ChatPromptCache>();

    // AI Security Services
    services.AddScoped<IAiToolSecurityService, AiToolSecurityService>();
    
    // AI Chat Embedding Service (Vector DB for semantic search)
    // Vector store behind the chat, transaction and help indexes. Hosted keeps Qdrant (the default);
    // self-host and dev set VectorStore:Provider=pgvector and use the app's own Postgres.
    var vectorProvider = VectorStoreProvider.Resolve(configuration["VectorStore:Provider"]);
    if (vectorProvider == VectorStoreProvider.PgVector)
    {
      // Same pool cap as EF: the vector store opens its own pool against the same server.
      var vectorConnection = CapPoolSize(configuration.GetConnectionString("BuoyAI")
                          ?? configuration.GetConnectionString("DefaultConnection"), configuration);
      services.AddSingleton<IVectorStore>(sp => new PgVectorStore(vectorConnection, sp.GetRequiredService<ILogger<PgVectorStore>>()));
    }
    else
    {
      // QdrantClient is only registered (Program.cs) when a qdrant connection string exists.
      services.AddSingleton<IVectorStore>(sp => new QdrantVectorStore(
        sp.GetService<Qdrant.Client.QdrantClient>(), sp.GetRequiredService<ILogger<QdrantVectorStore>>()));
    }
    services.AddScoped<IChatEmbeddingService, ChatEmbeddingService>();

    // Help Content Service (Vector DB for published blog/help article search - global)
    services.AddScoped<IHelpContentService, HelpContentService>();

    // Transaction Embedding Service (Vector DB for categorization similarity)
    services.AddScoped<ITransactionEmbeddingService, TransactionEmbeddingService>();

    // AI Memory Service (extracts and stores learned facts from conversations)
    services.AddScoped<IAiMemoryService, AiMemoryService>();
    
    // AI Conversation Service
    services.AddScoped<IAiConversationService, AiConversationService>();

    // WebAuthn/Passkey support is implemented via TwoFactorService using WebAuthnSettings from appsettings.json

    // Add memory cache for AI caching
    services.AddMemoryCache();

    // Per-user daily AI file-upload quota (issue #367)
    services.AddSingleton<BuoyAI.Core.Interfaces.IUploadQuotaService, Services.UploadQuotaService>();

    // Background Services
    //
    // Prod deploys are blue-green, so for about a minute two copies of the API share the
    // database. Scheduled and database-scanning jobs are registered with
    // AddJobLockedHostedService: only the copy holding the Postgres job lock runs them
    // (see BackgroundJobLock.cs). Plain AddHostedService is only for one-time idempotent
    // setup and for per-request in-memory queue consumers, whose work exists only in the
    // copy that took the request.
    services.AddBackgroundJobLock(
      skipDbContext || environment == "Testing"
        ? null
        : configuration.GetConnectionString("BuoyAI")
          ?? configuration.GetConnectionString("DefaultConnection"));
    // Receipt scanning (0045): matches receipts scanned before their charge synced.
    services.AddJobLockedHostedService<Services.Receipts.ReceiptRematchService>();
    // Hosted-only jobs: Plaid-direct sync, billing and growth emails, SaaS metrics, cross-user
    // statistics. One block, one switch. Everything else below runs in both editions.
    if (edition.IsHosted)
    {
      services.AddJobLockedHostedService<PlaidBackgroundImportService>();
      services.AddJobLockedHostedService<PlaidTransactionSyncService>();
      services.AddJobLockedHostedService<BroadcastDeliveryService>(); // admin broadcast delivery (#552)
      services.AddJobLockedHostedService<TrialReconciliationService>(); // attach Stripe to trials granted while Stripe was down
      services.AddJobLockedHostedService<TrialReminderService>(); // day-1 + day-4 connect-your-bank emails (opt-in via Email:TrialReminder:Enabled)
      services.AddJobLockedHostedService<LeadReminderService>(); // one nudge for unredeemed captured leads (opt-in via Email:LeadReminder:Enabled)
      services.AddJobLockedHostedService<ReferralRewardService>(); // grants referral coupons once the referee activates
      services.AddJobLockedHostedService<ReferralInvitePromptService>(); // one-off invite nudge (opt-in via Referral:InvitePrompt:Enabled)
      services.AddJobLockedHostedService<BusinessMetricsService>(); // MRR + subscriber KPI gauges → Grafana (#811); one copy, so dashboards don't double-count
      services.AddJobLockedHostedService<WrappedStatsService>(); // nightly Wrapped percentile cutoffs
    }
    // The background bank sync is the hosted Plaid-direct job above. A self-hosted server has no
    // Plaid, but still needs it to pull SimpleFIN on a schedule.
    if (edition.IsSelfHost && simpleFinEnabled)
    {
      services.AddJobLockedHostedService<PlaidTransactionSyncService>();
    }
    services.AddJobLockedHostedService<DataRetentionService>();
    services.AddJobLockedHostedService<SubscriptionDetectionService>();
    services.AddJobLockedHostedService<BudgetHealthCheckService>();
    services.AddHostedService<VectorCollectionInitService>(); // one-time, idempotent setup
    services.AddJobLockedHostedService<PushQueueDeliveryService>(); // quiet-hours push queue + token pruning (#190)
    services.AddJobLockedHostedService<EncryptionReEncryptionService>(); // legacy AES-CBC -> v2 AES-GCM, batched, idempotent
    services.AddJobLockedHostedService<BillReminderService>(); // upcoming-bill digest notifications (#190)
    services.AddJobLockedHostedService<WrappedRecapService>(); // monthly/yearly Money Wrapped delivery
    services.AddJobLockedHostedService<TransactionEmbeddingBackfillService>(); // warm the vector fast path from existing confirmations

    // Gamification: event-driven evaluation queue + consumer + daily reconciliation (#571)
    services.AddSingleton<GamificationEvaluationQueue>();
    services.AddSingleton<IGamificationEvaluationQueue>(
      sp => sp.GetRequiredService<GamificationEvaluationQueue>());
    services.AddHostedService<GamificationEvaluationService>(); // per-request in-memory queue
    services.AddJobLockedHostedService<GamificationDailyService>();

    // Off-thread AI insight generation: read paths enqueue on a cache miss instead of
    // blocking the request on a slow Gemini call (keeps api/AI/insights + savings/overview
    // under the latency alert). Same singleton-queue + hosted-consumer shape as above.
    services.AddSingleton<AiInsightQueue>();
    services.AddSingleton<IAiInsightQueue>(
      sp => sp.GetRequiredService<AiInsightQueue>());
    services.AddHostedService<AiInsightGenerationBackgroundService>(); // per-request in-memory queue

    // AI categorization queue: ingest paths signal, the drainer processes off the request
    // thread (plus a periodic sweep for rows queued before a restart).
    services.AddSingleton<AICategorizationQueueSignal>();
    services.AddSingleton<IAICategorizationQueueSignal>(
      sp => sp.GetRequiredService<AICategorizationQueueSignal>());
    // Locked: besides draining its in-memory signals it sweeps the database queue every few
    // minutes, and two sweepers would categorize (and pay Gemini for) the same rows twice.
    // Signals sent while this copy waits for the lock queue up and drain once it takes over.
    services.AddJobLockedHostedService<AICategorizationQueueDrainService>();

    return services;
  }
}

/// <summary>
/// Helper class to support Lazy&lt;T&gt; dependency injection for breaking circular dependencies.
/// This allows services to defer resolution of their dependencies until they are actually needed.
/// </summary>
internal class LazilyResolved<T> : Lazy<T> where T : class
{
  public LazilyResolved(IServiceProvider serviceProvider)
    : base(() => serviceProvider.GetRequiredService<T>())
  {
  }
}
