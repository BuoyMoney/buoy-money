using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Services;
using BuoyAI.Core.Utilities;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Telemetry;
using BuoyAI.Infrastructure.Utilities;
using OtpNet;

namespace BuoyAI.Infrastructure.Services;

public class AuthService : IAuthService
{
  private readonly IUserRepository _userRepository;
  private readonly IConfiguration _configuration;
  // Lazy: the aggregators (PlaidService cannot be built without Plaid keys, and is not registered
  // at all in self-host) must not be needed to sign in. Only account deletion reads this, inside a try.
  private readonly Lazy<IBankAggregatorRegistry> _aggregators;
  private readonly ILastSyncTracker _lastSyncTracker;
  private readonly ILogger<AuthService> _logger;
  private readonly BuoyAIDbContext _context;
  private readonly IEmailService _emailService;
  private readonly IEmailPreferencesService _emailPreferencesService;
  private readonly IHostEnvironment _environment;
  private readonly IServiceProvider _services;
  private readonly IEntitlementService _entitlements;
  private readonly INotificationService _notificationService;
  private readonly IDataRetentionManager _dataRetentionManager;
  private readonly IFamilyInvitationRepository _familyInvitationRepository;
  private readonly IGoogleTokenValidator _googleTokenValidator;
  private readonly IAppleTokenValidator _appleTokenValidator;
  private readonly IRefreshTokenService _refreshTokenService;
  private readonly BuoyAiMetrics _metrics;
  private readonly IEncryptionService _encryptionService;
  private readonly IAppleAuthClient _appleAuthClient;
  // Used to spin up an isolated DI scope (fresh DbContext) for fire-and-forget background work,
  // so it never shares the request-scoped DbContext.
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly IMarketingLeadService _marketingLeadService;

  public AuthService(
    IUserRepository userRepository,
    IConfiguration configuration,
    Lazy<IBankAggregatorRegistry> aggregators,
    ILastSyncTracker lastSyncTracker,
    ILogger<AuthService> logger,
    BuoyAIDbContext context,
    IEmailService emailService,
    IEmailPreferencesService emailPreferencesService,
    IHostEnvironment environment,
    IServiceProvider services,
    IEntitlementService entitlements,
    INotificationService notificationService,
    IDataRetentionManager dataRetentionManager,
    IFamilyInvitationRepository familyInvitationRepository,
    IGoogleTokenValidator googleTokenValidator,
    IAppleTokenValidator appleTokenValidator,
    IRefreshTokenService refreshTokenService,
    BuoyAiMetrics metrics,
    IEncryptionService encryptionService,
    IAppleAuthClient appleAuthClient,
    IServiceScopeFactory scopeFactory,
    IMarketingLeadService marketingLeadService)
  {
    _userRepository = userRepository;
    _configuration = configuration;
    _aggregators = aggregators;
    _lastSyncTracker = lastSyncTracker;
    _logger = logger;
    _context = context;
    _emailService = emailService;
    _emailPreferencesService = emailPreferencesService;
    _environment = environment;
    _services = services;
    _entitlements = entitlements;
    _notificationService = notificationService;
    _dataRetentionManager = dataRetentionManager;
    _familyInvitationRepository = familyInvitationRepository;
    _googleTokenValidator = googleTokenValidator;
    _appleTokenValidator = appleTokenValidator;
    _refreshTokenService = refreshTokenService;
    _metrics = metrics;
    _encryptionService = encryptionService;
    _appleAuthClient = appleAuthClient;
    _scopeFactory = scopeFactory;
    _marketingLeadService = marketingLeadService;
  }

  public async Task<LoginResponse?> LoginAsync(LoginRequest request)
  {
    _metrics.RecordLoginAttempt();
    try
    {
      var user = await _userRepository.GetByEmailAsync(request.Email);
      if (user == null)
      {
        _logger.LogWarning("Login attempt with non-existent email: {Email}", request.Email);
        _metrics.RecordLoginFailure("invalid_email");
        return null;
      }

      if (user.IsDeleted)
      {
        _logger.LogWarning("Login attempt for deleted user: {Email}", request.Email);
        _metrics.RecordLoginFailure("deleted_account");
        return null;
      }

      if (!VerifyPassword(request.Password, user.PasswordHash))
      {
        _logger.LogWarning("Invalid password for user: {Email}", request.Email);
        _metrics.RecordLoginFailure("invalid_password");
        return null;
      }

      var token = GenerateJwtTokenWithClaims(user.Id, user.Username, ResolveTierClaim(user));
      var expiresAt = AccessTokenExpiry(); // Short-lived; the refresh token carries the session

      // Implement login-triggered sync for free users
      await TriggerSyncIfNeeded(user);

      _metrics.RecordLoginSuccess();
      return new LoginResponse
      {
        Token = token,
        User = BuildProfileDto(user),
        ExpiresAt = expiresAt,
        PendingFamilyInvitations = await GetPendingInvitationsForEmailAsync(user.Email)
      };
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error during login for email: {Email}", request.Email);
      _metrics.RecordLoginFailure("exception");
      return null;
    }
  }

  public async Task<bool> VerifyPasswordAsync(int userId, string password)
  {
    if (string.IsNullOrEmpty(password))
      return false;

    try
    {
      var user = await _userRepository.GetByIdAsync(userId);
      // No password check for a missing/deleted user, or an account that has no local password
      // (Google/Apple/MFA-only accounts store an empty PasswordHash) — return false, never throw.
      if (user == null || user.IsDeleted || string.IsNullOrEmpty(user.PasswordHash))
        return false;

      return VerifyPassword(password, user.PasswordHash);
    }
    catch (Exception ex)
    {
      // A malformed stored hash makes BCrypt.Verify throw; treat any failure as "not verified"
      // rather than surfacing a 500 from what is a boolean check.
      _logger.LogWarning(ex, "Password verification failed unexpectedly for user {UserId}", userId);
      return false;
    }
  }

  /// <summary>
  /// The default trial length offered to every new account (14 days). <see cref="ApplyTrialPolicyAsync"/>
  /// still decides whether this email is entitled to a trial at all.
  /// </summary>
  private int ConfiguredTrialDays() => _configuration.GetValue<int>("Subscription:TrialPeriodDays", 14);

  /// <summary>
  /// How long a trial this email should be offered, honouring an unredeemed marketing-site
  /// lead (the one-month offer the popup made) and otherwise falling back to the
  /// configured default.
  ///
  /// This only decides the REQUESTED length. <see cref="ApplyTrialPolicyAsync"/> still has
  /// the final say, so an address that already burned its lifetime trial cannot resurrect
  /// one by filling in the popup.
  /// </summary>
  private async Task<int> ResolveRequestedTrialDaysAsync(string email)
  {
    var configured = ConfiguredTrialDays();
    try
    {
      var offered = await _marketingLeadService.GetUnredeemedOfferDaysAsync(email);
      if (offered is int days && days > configured)
      {
        _logger.LogInformation("Applying captured lead offer of {Days} trial days", days);
        return days;
      }
    }
    catch (Exception ex)
    {
      // A lead lookup must never block a registration. Worst case the visitor gets the
      // standard trial and we fix it by hand.
      _logger.LogError(ex, "Failed to resolve marketing-lead trial offer; using configured default");
    }
    return configured;
  }

  /// <summary>
  /// Marks a captured lead consumed, but only when a FRESH trial was actually granted.
  /// A resumed or withheld trial (returning email) did not spend the offer, so it stays
  /// available. Never throws: the account already exists at this point.
  /// </summary>
  private async Task RedeemLeadOfferIfGrantedAsync(TrialGrant grant, string email, int userId)
  {
    if (!grant.IsFreshGrant) return;
    try
    {
      await _marketingLeadService.MarkRedeemedAsync(email, userId);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to mark marketing lead redeemed for user {UserId}", userId);
    }
  }

  /// <summary>
  /// Creates the Stripe side of a granted trial: a customer plus a cardless subscription in
  /// <c>trialing</c>, so Stripe owns the lifecycle and sends the three-day expiry reminder.
  ///
  /// Runs for resumed trials too, not just fresh grants: a re-registering account resumes a
  /// trial that has no Stripe subscription behind it, and it needs one as much as a new one
  /// does. The provisioning service is idempotent and never throws, so registration is
  /// unaffected by a Stripe outage.
  /// </summary>
  private async Task ProvisionStripeTrialAsync(TrialGrant grant, int userId)
  {
    if (grant.Days <= 0) return;

    // Resolved on demand: provisioning needs Stripe, whose service throws at construction
    // without keys, and sign-up must not depend on it. No Stripe means no Stripe trial.
    ITrialProvisioningService provisioning;
    try
    {
      provisioning = _services.GetRequiredService<ITrialProvisioningService>();
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Trial provisioning unavailable; skipping the Stripe trial for user {UserId}", userId);
      return;
    }

    await provisioning.ProvisionStripeTrialAsync(userId, grant.Days);
  }

  /// <summary>
  /// Outcome of the trial policy. <paramref name="IsFreshGrant"/> distinguishes a brand-new
  /// trial from a resumed one, which is what decides whether a lead offer was spent.
  /// </summary>
  private readonly record struct TrialGrant(int Days, bool IsFreshGrant)
  {
    public static implicit operator int(TrialGrant g) => g.Days;
  }

  private async Task<TrialGrant> ApplyTrialPolicyAsync(User user, int requestedTrialDays)
  {
    if (IsSelfHost)
    {
      // No billing on self-host: no trial, no TrialHistory gating, and never a paywall.
      user.IsTrialActive = false;
      user.TrialStartDate = null;
      user.TrialEndDate = null;
      user.RequiresPaidPlan = false;
      return new TrialGrant(0, IsFreshGrant: false);
    }

    var priorTrial = await TrialHistoryRecorder.GetAsync(_context, user.Email);

    var now = DateTime.UtcNow;
    // HasUsedTrial is set in every branch: once an email has been entitled to a trial,
    // this account has consumed its lifetime trial and must never be granted a new one.
    user.HasUsedTrial = true;

    if (priorTrial != null)
    {
      // Returning user. If their ORIGINAL trial window hasn't elapsed yet, resume it up
      // to the original end date — they keep their remaining days but can never get more
      // than the one trial. Otherwise they must pick and pay for a plan (paywall).
      if (priorTrial.OriginalTrialEndsAt is DateTime originalEnd && originalEnd > now)
      {
        user.IsTrialActive = true;
        user.TrialStartDate = now;
        user.TrialEndDate = originalEnd;
        user.RequiresPaidPlan = false;
        var remaining = Math.Max(1, (int)Math.Ceiling((originalEnd - now).TotalDays));
        _logger.LogInformation(
          "Resuming trial for re-registering email; {Days} day(s) remain until {End}",
          remaining, originalEnd);
        // Resumed, not freshly granted: a captured lead offer is NOT spent here.
        return new TrialGrant(remaining, IsFreshGrant: false);
      }

      user.IsTrialActive = false;
      user.TrialStartDate = null;
      user.TrialEndDate = null;
      user.RequiresPaidPlan = true; // Original trial used up — show the paywall.
      _logger.LogInformation("Withholding free trial for re-registering email; prior trial expired");
      return new TrialGrant(0, IsFreshGrant: false);
    }

    if (requestedTrialDays <= 0)
    {
      user.IsTrialActive = false;
      user.TrialStartDate = null;
      user.TrialEndDate = null;
      return new TrialGrant(0, IsFreshGrant: false);
    }

    user.IsTrialActive = true;
    user.TrialStartDate = now;
    user.TrialEndDate = now.AddDays(requestedTrialDays);
    return new TrialGrant(requestedTrialDays, IsFreshGrant: true);
  }

  /// <summary>The sign-up started on the Cloud portal (<c>signupContext: "cloud"</c>). Never on self-host.</summary>
  private bool IsCloudSignup(string? signupContext) =>
    !IsSelfHost && string.Equals(signupContext?.Trim(), "cloud", StringComparison.OrdinalIgnoreCase);

  /// <summary>
  /// The trial policy for a new account: a Cloud-only sign-up gets no hosted trial at all (and does not use up the
  /// email's lifetime trial, so the hosted app can still offer one later); everyone else gets
  /// <see cref="ApplyTrialPolicyAsync"/>. A Cloud-only account opening the hosted app sees the normal "choose a plan"
  /// screen, the same as any account without a plan.
  /// </summary>
  private async Task<TrialGrant> ApplyNewAccountPolicyAsync(User user, string? signupContext, Func<Task<int>> requestedTrialDays)
  {
    if (!IsCloudSignup(signupContext))
    {
      return await ApplyTrialPolicyAsync(user, await requestedTrialDays());
    }

    user.CloudOnly = true;
    user.IsTrialActive = false;
    user.TrialStartDate = null;
    user.TrialEndDate = null;
    user.RequiresPaidPlan = true;
    return new TrialGrant(0, IsFreshGrant: false);
  }

  /// <summary>
  /// A Cloud-only account still needs a Stripe customer: it is the Cloud account id that Cloud billing and the
  /// server links use. Resolved on demand and never throws, like the trial: sign-up must not depend on Stripe.
  /// Without one the account works, and a first Cloud checkout creates the customer.
  /// </summary>
  private async Task EnsureCloudCustomerAsync(int userId)
  {
    ITrialProvisioningService provisioning;
    try
    {
      provisioning = _services.GetRequiredService<ITrialProvisioningService>();
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Stripe unavailable; skipping the Stripe customer for Cloud-only user {UserId}", userId);
      return;
    }

    await provisioning.EnsureStripeCustomerAsync(userId);
  }

  /// <summary>
  /// Records that a trial was granted to this email so that a future re-registration
  /// (after deletion) cannot obtain another. Idempotent per email hash. Call only after
  /// the user row exists (so <paramref name="userId"/> is assigned); the caller owns the
  /// surrounding transaction/commit.
  /// </summary>
  private Task RecordTrialGrantAsync(string email, int userId, string source, DateTime? trialEndsAt) =>
    TrialHistoryRecorder.RecordGrantAsync(_context, email, userId, source, trialEndsAt);

  /// <summary>Which parts of the new-account sequence a call runs. Always run in this order.</summary>
  [Flags]
  private enum PostSignUpSteps
  {
    /// <summary>Durable trial-history row (only when a trial was granted). Inside the sign-up transaction.</summary>
    RecordGrant = 1,
    /// <summary>Spend the captured lead offer, then create the Stripe side of the trial.</summary>
    LeadAndStripe = 2,
    /// <summary>Default email preferences, then the day-0 email.</summary>
    Notify = 4,
    All = RecordGrant | LeadAndStripe | Notify,
  }

  /// <summary>
  /// The one new-account sequence shared by the email, Google and Apple sign-up paths, after
  /// <see cref="ApplyTrialPolicyAsync"/> has stamped the trial and the user row exists:
  /// record the grant, redeem a lead offer, provision the Stripe trial, create email
  /// preferences, send the first email. The steps always run in that order; the paths differ
  /// only in where the transaction commits, so each passes the subset that belongs on its
  /// side of the commit (email: grant and Stripe inside, notify after; Google: all inside;
  /// Apple: grant inside, the rest after). Preference and email failures never fail sign-up.
  /// </summary>
  /// <param name="source">"email", "google" or "apple": the trial-history source and log label.</param>
  private async Task RunPostSignUpAsync(User user, TrialGrant grant, string source, PostSignUpSteps steps)
  {
    if (steps.HasFlag(PostSignUpSteps.RecordGrant) && grant.Days > 0)
    {
      await RecordTrialGrantAsync(user.Email, user.Id, source, user.TrialEndDate);
    }

    if (steps.HasFlag(PostSignUpSteps.LeadAndStripe))
    {
      if (user.CloudOnly)
      {
        // No trial, so no lead offer is spent; the customer is all Cloud billing needs.
        await EnsureCloudCustomerAsync(user.Id);
      }
      else
      {
        await RedeemLeadOfferIfGrantedAsync(grant, user.Email, user.Id);
        await ProvisionStripeTrialAsync(grant, user.Id);
      }
    }

    if (steps.HasFlag(PostSignUpSteps.Notify))
    {
      var label = source switch { "google" => "Google user", "apple" => "Apple user", _ => "user" };
      try
      {
        await _emailPreferencesService.CreateDefaultPreferencesAsync(user.Id);
        _logger.LogInformation("Created default email preferences for new {Label} {UserId}", label, user.Id);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Failed to create email preferences for {Label} {UserId}", label, user.Id);
      }

      // The welcome and trial emails are about the hosted app; a Cloud-only account gets neither.
      if (!user.CloudOnly)
      {
        await SendFirstEmailAsync(user, grant.Days);
      }
    }
  }

  /// <summary>Billing for the Stripe cleanup in account deletion, or null when it cannot be built (no Stripe keys).</summary>
  private IBillingService? TryResolveBilling(int userId)
  {
    try
    {
      return _services.GetRequiredService<IBillingService>();
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Billing unavailable; skipping Stripe cancel and customer delete for user {UserId}", userId);
      return null;
    }
  }

  /// <summary>
  /// Self-host only (the gate is not registered on hosted): applies the registration lock, makes the
  /// first account the admin, and with no mail server marks the address verified, because no
  /// verification email could ever arrive. Throws <see cref="RegistrationClosedException"/> when closed.
  /// </summary>
  private async Task<int?> AdmitNewAccountAsync(User user, string? inviteCode)
  {
    if (!IsSelfHost) return null;
    if (_services.GetService<IEmailTransport>() is { IsConfigured: false } && !user.IsEmailVerified)
    {
      user.IsEmailVerified = true;
      user.EmailVerifiedAt = DateTime.UtcNow;
    }
    var gate = _services.GetService<RegistrationGate>();
    return gate == null ? null : await gate.AdmitAsync(user, inviteCode);
  }

  private async Task ConsumeInviteAsync(int? inviteId, int userId)
  {
    if (inviteId == null) return;
    await _services.GetRequiredService<RegistrationGate>().ConsumeInviteAsync(inviteId, userId);
  }

  public async Task<LoginResponse?> RegisterAsync(RegisterRequest request)
  {
    try
    {
      // Validate input
      if (string.IsNullOrEmpty(request.Email) || string.IsNullOrEmpty(request.Password) ||
          string.IsNullOrEmpty(request.FirstName) || string.IsNullOrEmpty(request.LastName))
      {
        _logger.LogWarning("Registration attempt with missing required fields");
        return null;
      }

      // Validate password strength
      var passwordValidation = PasswordValidationService.ValidatePassword(request.Password);
      if (!passwordValidation.IsValid)
      {
        _logger.LogWarning("Registration attempt with weak password: {Errors}", string.Join(", ", passwordValidation.Errors));
        return null;
      }

      // Validate email format
      if (!IsValidEmail(request.Email))
      {
        _logger.LogWarning("Registration attempt with invalid email format: {Email}", request.Email);
        return null;
      }

      // Use execution strategy to wrap transaction (required by NpgsqlRetryingExecutionStrategy)
      var strategy = _context.Database.CreateExecutionStrategy();
      User? createdUser = null;
      TrialGrant emailGrant = default;

      try
      {
        await strategy.ExecuteAsync(async () =>
        {
          await using var transaction = await _context.Database.BeginTransactionAsync();

          var existingUser = await _userRepository.GetByEmailAsync(request.Email);
          if (existingUser != null)
          {
            if (existingUser.IsDeleted)
            {
              await ClearDeletedUserIdentifiersAsync(existingUser);
            }
            else
            {
              _logger.LogWarning("Registration attempt with existing email: {Email}", request.Email);
              return;
            }
          }

          createdUser = new User
          {
            Email = request.Email.ToLowerInvariant().Trim(),
            FirstName = SanitizeInput(request.FirstName),
            LastName = SanitizeInput(request.LastName),
            Username = await GenerateUniqueUsername(request.Email),
            PasswordHash = HashPassword(request.Password),
            IsGoogleUser = false,
            Currency = "USD",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
          };

          // Grant a trial only if this email has never had one (survives deletion). The requested length honours
          // the one-month offer captured by the marketing-site popup. A Cloud-only sign-up gets no trial.
          emailGrant = await ApplyNewAccountPolicyAsync(createdUser, request.SignupContext,
            () => ResolveRequestedTrialDaysAsync(request.Email));

          var emailInviteId = await AdmitNewAccountAsync(createdUser, request.InviteCode);
          createdUser = await _userRepository.CreateAsync(createdUser);
          await ConsumeInviteAsync(emailInviteId, createdUser.Id);
          await RunPostSignUpAsync(createdUser, emailGrant, "email",
            PostSignUpSteps.RecordGrant | PostSignUpSteps.LeadAndStripe);
          await transaction.CommitAsync();
        });
      }
      catch (DbUpdateException)
      {
        _logger.LogWarning("Concurrent registration detected for email: {Email}", request.Email);
        return null;
      }

      if (createdUser == null) return null;

      await RunPostSignUpAsync(createdUser, emailGrant, "email", PostSignUpSteps.Notify);

      var token = GenerateJwtTokenWithClaims(createdUser.Id, createdUser.Username, ResolveTierClaim(createdUser));
      var expiresAt = AccessTokenExpiry();

      _metrics.RecordSignup();
      return new LoginResponse
      {
        Token = token,
        User = BuildProfileDto(createdUser),
        ExpiresAt = expiresAt,
        // RegisterAsync only ever returns after creating the account.
        IsNewUser = true,
        PendingFamilyInvitations = await GetPendingInvitationsForEmailAsync(createdUser.Email)
      };
    }
    catch (RegistrationClosedException)
    {
      // Control flow for a closed self-hosted server: the controller turns it into a 403.
      throw;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error during registration for email: {Email}", request.Email);
      return null;
    }
  }

  public async Task<LoginResponse?> GoogleLoginAsync(GoogleLoginRequest request)
  {
    _metrics.RecordLoginAttempt();
    try
    {
      // Verify the Google ID token
      var payload = await _googleTokenValidator.ValidateAsync(request.IdToken);

      if (payload == null)
      {
        _logger.LogWarning("Invalid Google ID token provided");
        return null;
      }

      // Validate email format from Google payload
      if (string.IsNullOrEmpty(payload.Email) || !IsValidEmail(payload.Email))
      {
        _logger.LogWarning("Invalid email format from Google OAuth: {Email}", payload.Email);
        return null;
      }

      // Validate essential Google payload data
      if (string.IsNullOrEmpty(payload.Subject))
      {
        _logger.LogWarning("Missing Google subject ID in OAuth payload");
        return null;
      }

      // Refuse an unverified email. Google returns email_verified=false for e.g. a
      // non-Gmail address a user attached to their Google account but never verified.
      // Trusting it would let an attacker whose Google account carries the victim's
      // (unverified) email be resolved to — or linked onto — the victim's password
      // account by the email match below, taking it over without the password or MFA.
      // Only a provider-verified email may resolve or link an account.
      if (!payload.EmailVerified)
      {
        _logger.LogWarning("Google login rejected: email not verified by Google ({Email})", payload.Email);
        _metrics.RecordLoginFailure("google_email_unverified");
        return null;
      }

      // Use execution strategy to wrap transaction (required by NpgsqlRetryingExecutionStrategy)
      var strategy = _context.Database.CreateExecutionStrategy();
      User? resolvedUser = null;
      GoogleRegistrationRejection? rejection = null;
      bool noAccount = false;
      bool created = false;

      try
      {
        await strategy.ExecuteAsync(async () =>
        {
          await using var transaction = await _context.Database.BeginTransactionAsync();

          var user = await _userRepository.GetByGoogleIdAsync(payload.Subject);

          if (user != null && user.IsDeleted)
          {
            await ClearDeletedUserIdentifiersAsync(user);
            user = null;
          }

          if (user == null)
          {
            user = await _userRepository.GetByEmailAsync(payload.Email);

            if (user != null && user.IsDeleted)
            {
              await ClearDeletedUserIdentifiersAsync(user);
              user = null;
            }

            if (user == null)
            {
              // Login flow must never auto-create an account. Signal "no account"
              // so the controller can return 404 and the client can route to register.
              if (!request.IsRegistration)
              {
                _logger.LogInformation("Google login attempted with no existing account: {Email}", payload.Email);
                noAccount = true;
                return;
              }

              if (IsSuspiciousEmailDomain(payload.Email))
              {
                _logger.LogWarning("Suspicious email domain blocked for Google OAuth: {Email}", payload.Email);
                rejection = GoogleRegistrationRejection.BlockedDomain;
                return;
              }

              var recentGoogleRegistrations = await GetRecentGoogleRegistrationsCount();
              if (recentGoogleRegistrations > 100)
              {
                _logger.LogWarning("Rate limit exceeded for Google OAuth registrations");
                rejection = GoogleRegistrationRejection.RateLimited;
                return;
              }

              user = new User
              {
                GoogleId = payload.Subject,
                Email = payload.Email.ToLowerInvariant().Trim(),
                FirstName = SanitizeInput(payload.GivenName) ?? "",
                LastName = SanitizeInput(payload.FamilyName) ?? "",
                Username = await GenerateUniqueUsername(payload.Email),
                ProfilePictureUrl = IsValidUrl(payload.Picture) ? payload.Picture : null,
                IsGoogleUser = true,
                PasswordHash = "",
                Currency = "USD",
                // Google has already verified the email address.
                IsEmailVerified = true,
                EmailVerifiedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
              };

              // Grant a trial only if this email has never had one (survives deletion). A Cloud-only sign-up gets none.
              var googleGrant = await ApplyNewAccountPolicyAsync(user, request.SignupContext,
                () => ResolveRequestedTrialDaysAsync(payload.Email));

              int? googleInviteId;
              try
              {
                googleInviteId = await AdmitNewAccountAsync(user, request.InviteCode);
              }
              catch (RegistrationClosedException)
              {
                rejection = GoogleRegistrationRejection.RegistrationClosed;
                return;
              }

              _logger.LogInformation("Creating new Google OAuth user: {Email}", payload.Email);
              user = await _userRepository.CreateAsync(user);
              created = true;
              await ConsumeInviteAsync(googleInviteId, user.Id);

              await RunPostSignUpAsync(user, googleGrant, "google", PostSignUpSteps.All);
            }
            else
            {
              user.GoogleId = payload.Subject;
              user.ProfilePictureUrl = payload.Picture;
              user.IsGoogleUser = true;
              user.UpdatedAt = DateTime.UtcNow;

              await _userRepository.UpdateAsync(user);
            }
          }
          else
          {
            // Keep stored emails lowercased/trimmed (consistent with account creation) so the
            // case-insensitive lookup stays sound and we don't drift a user's email casing.
            user.Email = payload.Email.ToLowerInvariant().Trim();
            user.FirstName = payload.GivenName ?? user.FirstName;
            user.LastName = payload.FamilyName ?? user.LastName;
            user.ProfilePictureUrl = payload.Picture;
            user.UpdatedAt = DateTime.UtcNow;

            await _userRepository.UpdateAsync(user);
          }

          await transaction.CommitAsync();
          resolvedUser = user;
        });
      }
      catch (DbUpdateException)
      {
        _logger.LogWarning("Concurrent registration detected for Google OAuth: {Email}", payload.Email);
        return null;
      }

      if (noAccount) throw new GoogleAccountNotFoundException(payload.Email);

      // Surfaced as distinct statuses (403 / 429) rather than folded into the 401 that
      // also means "the token itself was bad". An alert cannot tell an outage from a
      // throwaway signup if all three answer the same code.
      if (rejection.HasValue)
      {
        throw new GoogleRegistrationRejectedException(
          rejection.Value,
          rejection.Value switch
          {
            GoogleRegistrationRejection.RateLimited => "Too many sign-ups right now. Please try again shortly.",
            GoogleRegistrationRejection.RegistrationClosed =>
              "This server is not accepting new accounts. Ask the person who runs it for an invite code.",
            _ => "This email domain is not accepted.",
          });
      }

      if (resolvedUser == null) return null;

      var token = GenerateJwtTokenWithClaims(resolvedUser.Id, resolvedUser.Username, ResolveTierClaim(resolvedUser));
      var expiresAt = AccessTokenExpiry();

      _metrics.RecordLoginSuccess();
      return new LoginResponse
      {
        Token = token,
        User = BuildProfileDto(resolvedUser),
        ExpiresAt = expiresAt,
        IsNewUser = created,
        PendingFamilyInvitations = await GetPendingInvitationsForEmailAsync(resolvedUser.Email)
      };
    }
    catch (GoogleAccountNotFoundException)
    {
      // Expected control-flow signal for the login-with-no-account case; let it
      // propagate so the controller can translate it to a 404.
      _metrics.RecordLoginFailure("google_no_account");
      throw;
    }
    catch (GoogleRegistrationRejectedException ex)
    {
      // Also an expected control-flow signal, not a fault. Without this the general
      // handler below would swallow it back into a null and the caller would answer 401
      // again — recreating the exact conflation this separation exists to remove.
      _metrics.RecordLoginFailure($"google_{ex.Reason.ToString().ToLowerInvariant()}");
      throw;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error during Google login");
      _metrics.RecordLoginFailure("google_exception");
      return null;
    }
  }

  public async Task<OidcResolvedUser> ResolveOidcUserAsync(OidcIdentity identity, bool autoRegister, string? inviteCode)
  {
    _metrics.RecordLoginAttempt();
    if (string.IsNullOrWhiteSpace(identity.Issuer) || string.IsNullOrWhiteSpace(identity.Subject))
    {
      _metrics.RecordLoginFailure("oidc_invalid");
      throw new OidcLoginException("invalid_response", "The sign-in provider sent an incomplete answer.");
    }
    if (string.IsNullOrWhiteSpace(identity.Email) || !IsValidEmail(identity.Email))
    {
      _metrics.RecordLoginFailure("oidc_no_email");
      throw new OidcLoginException("email_unverified", "The sign-in provider did not share an email address.");
    }
    // The caller already refused an unverified email unless the operator chose to trust the
    // provider (Oidc:TrustUnverifiedEmail). Matching an account by an unverified email would let
    // anyone who can type the victim's address at the provider take the account over.
    if (!identity.EmailVerified)
    {
      _metrics.RecordLoginFailure("oidc_email_unverified");
      throw new OidcLoginException("email_unverified", "Your email is not verified at the sign-in provider.");
    }

    var email = identity.Email.Trim().ToLowerInvariant();
    var strategy = _context.Database.CreateExecutionStrategy();
    User? resolved = null;
    var created = false;
    OidcLoginException? refusal = null;

    try
    {
      await strategy.ExecuteAsync(async () =>
      {
        resolved = null;
        created = false;
        refusal = null;
        await using var transaction = await _context.Database.BeginTransactionAsync();
        var now = DateTime.UtcNow;

        // 1. A known provider account: match by issuer + subject, whatever its email is today.
        var link = await _context.UserExternalLogins
          .FirstOrDefaultAsync(l => l.Issuer == identity.Issuer && l.Subject == identity.Subject);
        if (link != null)
        {
          var linked = await _userRepository.GetByIdAsync(link.UserId);
          if (linked != null && !linked.IsDeleted)
          {
            link.Email = email;
            link.LastLoginAt = now;
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
            resolved = linked;
            return;
          }
          // The account behind the link was deleted: forget the link and start over.
          _context.UserExternalLogins.Remove(link);
          await _context.SaveChangesAsync();
        }

        // 2. First sign-in with this provider account: link to the account with this verified email.
        var user = await _userRepository.GetByEmailAsync(email);
        if (user != null && user.IsDeleted)
        {
          await ClearDeletedUserIdentifiersAsync(user);
          user = null;
        }

        if (user == null)
        {
          // 3. No account. Only the server's own admin email or an invite code may create one,
          // unless the operator turned on automatic sign-up (still bound by the registration rules).
          var operatorEmail = AdminPolicy.IsAdminEmail(_configuration, email);
          if (!autoRegister && !operatorEmail && string.IsNullOrWhiteSpace(inviteCode))
          {
            refusal = new OidcLoginException("no_account",
              "No account uses this email. Ask the person who runs this server for an invite.");
            return;
          }

          var (firstName, lastName) = SplitOidcName(identity);
          user = new User
          {
            Email = email,
            FirstName = firstName,
            LastName = lastName,
            Username = await GenerateUniqueUsername(email),
            PasswordHash = "",
            IsGoogleUser = false,
            Currency = "USD",
            // The provider verified the address.
            IsEmailVerified = true,
            EmailVerifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now
          };
          var grant = await ApplyTrialPolicyAsync(user, 0);

          int? inviteId;
          try
          {
            inviteId = await AdmitNewAccountAsync(user, inviteCode);
          }
          catch (RegistrationClosedException ex)
          {
            refusal = new OidcLoginException("registration_closed", ex.Message);
            return;
          }

          user = await _userRepository.CreateAsync(user);
          created = true;
          await ConsumeInviteAsync(inviteId, user.Id);
          await RunPostSignUpAsync(user, grant, "oidc", PostSignUpSteps.All);
        }

        _context.UserExternalLogins.Add(new UserExternalLogin
        {
          UserId = user.Id,
          Issuer = identity.Issuer,
          Subject = identity.Subject,
          Email = email,
          CreatedAt = now,
          LastLoginAt = now
        });
        await _context.SaveChangesAsync();
        await transaction.CommitAsync();
        resolved = user;
      });
    }
    catch (DbUpdateException ex)
    {
      // Two first sign-ins racing on the same provider account: the unique (issuer, subject) index
      // lets one win. Trying again finds the link.
      _logger.LogWarning(ex, "Concurrent single sign-on link for {Email}", email);
      _metrics.RecordLoginFailure("oidc_conflict");
      throw new OidcLoginException("invalid_response", "Sign-in was interrupted. Please try again.");
    }

    if (refusal != null)
    {
      _metrics.RecordLoginFailure($"oidc_{refusal.Code}");
      throw refusal;
    }

    if (resolved == null)
    {
      _metrics.RecordLoginFailure("oidc_unresolved");
      throw new OidcLoginException("invalid_response", "Sign-in failed. Please try again.");
    }

    if (created)
    {
      _logger.LogInformation("Created account {UserId} from single sign-on", resolved.Id);
      _metrics.RecordSignup();
    }
    return new OidcResolvedUser(resolved.Id, created);
  }

  private static (string First, string Last) SplitOidcName(OidcIdentity identity)
  {
    var first = SanitizeInput(identity.GivenName);
    var last = SanitizeInput(identity.FamilyName);
    if (first.Length == 0 && !string.IsNullOrWhiteSpace(identity.Name))
    {
      var parts = identity.Name.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
      first = SanitizeInput(parts[0]);
      if (last.Length == 0 && parts.Length > 1) last = SanitizeInput(parts[1]);
    }
    return (first, last);
  }

  public async Task<LoginResponse?> IssueLoginForUserAsync(int userId, bool isNewUser)
  {
    var user = await _userRepository.GetByIdAsync(userId);
    if (user == null || user.IsDeleted) return null;

    _metrics.RecordLoginSuccess();
    return new LoginResponse
    {
      Token = GenerateJwtTokenWithClaims(user.Id, user.Username, ResolveTierClaim(user)),
      User = BuildProfileDto(user),
      ExpiresAt = AccessTokenExpiry(),
      IsNewUser = isNewUser,
      PendingFamilyInvitations = await GetPendingInvitationsForEmailAsync(user.Email)
    };
  }

  public async Task<LoginResponse?> AppleLoginAsync(AppleLoginRequest request)
  {
    _metrics.RecordLoginAttempt();
    var appleCreated = false;
    try
    {
      // The identity token is the only hard requirement. Do NOT require
      // request.UserIdentifier: Apple's web (JS SDK) flow returns none, and the
      // authoritative user id is the verified token's `sub` (see below) — the
      // client-supplied identifier is never trusted regardless.
      if (string.IsNullOrEmpty(request.IdentityToken))
      {
        _logger.LogWarning("Missing identity token in Apple login request");
        return null;
      }

      // A nonce is REQUIRED — it binds the identity token to a fresh sign-in request
      // and is our replay protection. Our clients always generate and send one, so an
      // absent nonce is treated as an invalid login rather than silently skipping the
      // check (which would let a captured token be replayed).
      if (string.IsNullOrEmpty(request.Nonce))
      {
        _logger.LogWarning("Apple login rejected: missing nonce (replay protection)");
        _metrics.RecordLoginFailure("apple_missing_nonce");
        return null;
      }

      // Verify the identity token against Apple's JWKS (signature, issuer,
      // audience, expiry, and nonce). Only the verified `sub` and email are trusted.
      var applePayload = await _appleTokenValidator.ValidateAsync(
        request.IdentityToken,
        request.Nonce);

      if (applePayload == null)
      {
        _logger.LogWarning("Apple identity token failed verification");
        _metrics.RecordLoginFailure("apple_invalid_token");
        return null;
      }

      // The verified `sub` is authoritative; never trust the client-supplied identifier.
      var appleUserId = applePayload.Subject;
      var tokenEmail = applePayload.Email;

      // Use email from the verified token, or from the request (Apple only sends
      // the email in the request payload on the very first sign-in).
      var email = tokenEmail ?? request.Email;
      
      if (string.IsNullOrEmpty(email))
      {
        _logger.LogWarning("No email available from Apple Sign-In");
        return null;
      }

      // Resolve any existing account. A soft-deleted match (by Apple ID or email) is
      // anonymized and treated as a fresh registration — mirrors the Google path and
      // prevents a stale AppleId/email from permanently locking the user out.
      var user = await _userRepository.GetByAppleIdAsync(appleUserId);
      if (user != null && user.IsDeleted)
      {
        await ClearDeletedUserIdentifiersAsync(user);
        user = null;
      }
      // Only fall back to resolving/linking by EMAIL when Apple has verified that email.
      // An unverified email must never resolve to — or be linked onto — an existing
      // account (same account-takeover class as the Google path). When it's unverified we
      // skip the email lookup entirely; the verified Apple `sub` is still the identity, so
      // a genuinely new user is created below rather than logged into someone else's account.
      if (user == null && applePayload.EmailVerified)
      {
        user = await _userRepository.GetByEmailAsync(email);
        if (user != null && user.IsDeleted)
        {
          await ClearDeletedUserIdentifiersAsync(user);
          user = null;
        }
      }

      if (user == null)
      {
        // We deliberately skipped the email lookup above for an unverified Apple email
        // (to prevent account takeover), but a genuinely-new insert would still collide
        // with an existing account's UNIQUE email index and throw a DbUpdateException ->
        // HTTP 500. Detect that collision up front and fail closed cleanly instead of
        // attempting a doomed insert. A verified email that collided would already have
        // resolved `user` above, so this only trips on the unverified path.
        var emailCollision = await _userRepository.GetByEmailAsync(email);
        if (emailCollision != null && !emailCollision.IsDeleted)
        {
          _logger.LogWarning(
            "apple_email_unverified_collision: refusing to create Apple account; email already belongs to an existing account");
          _metrics.RecordLoginFailure("apple_email_unverified_collision");
          return null;
        }

        // Create new user
        var newUser = new User
        {
          AppleId = appleUserId,
          Email = email.ToLowerInvariant().Trim(),
          FirstName = request.FirstName ?? "",
          LastName = request.LastName ?? "",
          Username = await GenerateUniqueUsername(email),
          IsAppleUser = true,
          PasswordHash = "", // No password for Apple users
          Currency = "USD",
          // Trust Apple's own email_verified claim rather than assuming verified: a
          // relay/unverified email should not be marked verified on the new account.
          IsEmailVerified = applePayload.EmailVerified,
          EmailVerifiedAt = applePayload.EmailVerified ? DateTime.UtcNow : null,
          CreatedAt = DateTime.UtcNow,
          UpdatedAt = DateTime.UtcNow
        };

        // Grant a trial only if this email has never had one (survives deletion). A Cloud-only sign-up gets none.
        var appleGrant = await ApplyNewAccountPolicyAsync(newUser, request.SignupContext,
          () => ResolveRequestedTrialDaysAsync(newUser.Email));
        // Apple sign-in is not offered on self-host; if it is reached anyway the gate still applies,
        // inside the creating transaction so the first-account lock is held through the insert.
        var strategy = _context.Database.CreateExecutionStrategy();
        try
        {
          await strategy.ExecuteAsync(async () =>
          {
            await using var transaction = await _context.Database.BeginTransactionAsync();
            await AdmitNewAccountAsync(newUser, null);
            // Create the user and record the trial atomically so we can never end up with an
            // active trial but no TrialHistory row (which would allow a second free trial).
            await _userRepository.CreateAsync(newUser);
            await RunPostSignUpAsync(newUser, appleGrant, "apple", PostSignUpSteps.RecordGrant);
            await transaction.CommitAsync();
          });
        }
        catch (RegistrationClosedException)
        {
          _logger.LogWarning("Apple sign-up refused: registration is closed on this server");
          return null;
        }
        catch (DbUpdateException)
        {
          _logger.LogWarning("Concurrent Apple registration detected for email: {Email}", email);
          return null;
        }

        // Apple sign-up used to send nothing at all on day 0; the shared sequence sends it.
        await RunPostSignUpAsync(newUser, appleGrant, "apple",
          PostSignUpSteps.LeadAndStripe | PostSignUpSteps.Notify);

        user = newUser;
        appleCreated = true;
        _logger.LogInformation("Created new user via Apple Sign-In: {UserId}", user.Id);
      }
      else if (user.AppleId != appleUserId)
      {
        // Link Apple ID to an existing (non-deleted) account found by email.
        user.AppleId = appleUserId;
        user.IsAppleUser = true;
        if (!string.IsNullOrEmpty(request.FirstName))
          user.FirstName = request.FirstName;
        if (!string.IsNullOrEmpty(request.LastName))
          user.LastName = request.LastName;
        user.UpdatedAt = DateTime.UtcNow;
        await _userRepository.UpdateAsync(user);
        _logger.LogInformation("Linked Apple ID to existing user: {UserId}", user.Id);
      }
      else
      {
        // Existing Apple user — update last login.
        user.UpdatedAt = DateTime.UtcNow;
        await _userRepository.UpdateAsync(user);
      }

      if (user.IsDeleted)
      {
        _logger.LogWarning("Apple login attempt for deleted user: {Email}", user.Email);
        _metrics.RecordLoginFailure("deleted_account");
        return null;
      }

      // Capture Apple's refresh token (encrypted) so the Sign-in-with-Apple grant can be
      // revoked at Apple when the user deletes their account. Best-effort — never fail login.
      await TryCaptureAppleRefreshTokenAsync(user, request.AuthorizationCode);

      var token = GenerateJwtTokenWithClaims(user.Id, user.Username, ResolveTierClaim(user));
      var expiresAt = AccessTokenExpiry();

      _metrics.RecordLoginSuccess();
      return new LoginResponse
      {
        Token = token,
        User = BuildProfileDto(user),
        ExpiresAt = expiresAt,
        IsNewUser = appleCreated
      };
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error during Apple login");
      _metrics.RecordLoginFailure("apple_exception");
      return null;
    }
  }

  /// <summary>
  /// Exchanges the Apple authorization code for a refresh token and stores it (encrypted)
  /// on the user so the grant can be revoked when the account is deleted. Best-effort: a
  /// failure here never blocks login, and it no-ops when Apple auth is unconfigured.
  /// </summary>
  private async Task TryCaptureAppleRefreshTokenAsync(User user, string? authorizationCode)
  {
    if (!_appleAuthClient.IsConfigured || string.IsNullOrEmpty(authorizationCode))
      return;
    try
    {
      var refreshToken = await _appleAuthClient.ExchangeAuthorizationCodeAsync(authorizationCode);
      if (string.IsNullOrEmpty(refreshToken))
        return;
      user.AppleRefreshToken = _encryptionService.Encrypt(refreshToken);
      await _userRepository.UpdateAsync(user);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to capture Apple refresh token for user {UserId}", user.Id);
    }
  }

  /// <summary>
  /// Revokes the user's Sign-in-with-Apple grant at Apple using the stored (encrypted)
  /// refresh token — Apple's account-deletion requirement. Best-effort; no-ops when Apple
  /// auth is unconfigured or no token was ever captured.
  /// </summary>
  private async Task TryRevokeAppleGrantAsync(User user)
  {
    if (string.IsNullOrEmpty(user.AppleRefreshToken) || !_appleAuthClient.IsConfigured)
      return;
    try
    {
      var refreshToken = _encryptionService.Decrypt(user.AppleRefreshToken);
      var revoked = await _appleAuthClient.RevokeAsync(refreshToken);
      _logger.LogInformation("Apple grant revocation for user {UserId}: {Result}",
        user.Id, revoked ? "succeeded" : "failed");
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to revoke Apple grant for user {UserId} during account deletion", user.Id);
    }
  }

  public async Task<UserProfileDto?> GetUserProfileAsync(int userId)
  {
    try
    {
      var user = await _userRepository.GetByIdAsync(userId);
      if (user == null)
        return null;

      var dto = BuildProfileDto(user);

      // Enrich with family role if user belongs to a family
      if (user.FamilyId.HasValue)
      {
        var familyMember = await _context.FamilyMembers
            .FirstOrDefaultAsync(fm => fm.UserId == user.Id && fm.FamilyId == user.FamilyId.Value);
        dto.FamilyRole = familyMember?.Role.ToString();
      }

      return dto;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting user profile for userId: {UserId}", userId);
      return null;
    }
  }

  /// <summary>
  /// Creates a <see cref="UserProfileDto"/> from a <see cref="User"/> entity with base fields.
  /// Additional enrichment, such as family role, is applied by <see cref="GetUserProfileAsync(int)"/>
  /// after this base DTO is created.
  /// </summary>
  private UserProfileDto BuildProfileDto(User user) => new()
  {
    Id = user.Id,
    Username = user.Username,
    Email = user.Email,
    FirstName = user.FirstName,
    LastName = user.LastName,
    Currency = user.Currency,
    WeekStartDay = user.WeekStartDay,
    ProfilePictureUrl = user.ProfilePictureUrl,
    AvatarUpdatedAt = user.AvatarUpdatedAt,
    IsGoogleUser = user.IsGoogleUser,
    IsAppleUser = user.IsAppleUser,
    HasPassword = !string.IsNullOrEmpty(user.PasswordHash),
    // Operator emails (Admin:Emails allowlist) surface as Admin so the client unlocks
    // internal tools; Admin is never purchasable.
    SubscriptionTier = AdminPolicy.IsAdminEmail(_configuration, user.Email)
      ? SubscriptionTier.Admin.ToString()
      : ResolveTierClaim(user)?.ToString() ?? (user.IsTrialActive ? "Trial" : "Free"),
    MfaEnabled = user.HasMfaEnabled,
    TrialEndsAt = user.TrialEndDate,
    CreatedAt = user.CreatedAt,
    FamilyId = user.FamilyId,
    RequiresPaidPlan = !IsSelfHost && user.RequiresPaidPlan,
    IsEmailVerified = user.IsEmailVerified,
    AiConsentGrantedAt = AiConsent.VisibleGrantedAt(user),
    AiConsentWithdrawnAt = user.AiConsentWithdrawnAt,
  };

  private async Task<List<PendingFamilyInvitationDto>> GetPendingInvitationsForEmailAsync(string email)
  {
    try
    {
      var invitations = (await _familyInvitationRepository.GetByEmailAsync(email)).ToList();
      if (invitations.Count == 0) return new List<PendingFamilyInvitationDto>();

      return invitations.Select(inv => new PendingFamilyInvitationDto
      {
        InvitationToken = inv.InvitationToken,
        FamilyName = inv.Family?.Name ?? "Family",
        InvitedByName = inv.InvitedByUser != null
          ? $"{inv.InvitedByUser.FirstName} {inv.InvitedByUser.LastName}"
          : "Family Owner",
        InvitedRole = inv.InvitedRole.ToString(),
        ExpiresAt = inv.ExpiresAt
      }).ToList();
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to look up pending invitations for {Email}", email);
      return new List<PendingFamilyInvitationDto>();
    }
  }

  public string GenerateJwtToken(int userId, string username)
  {
    // Default implementation without subscription tier (for backward compatibility)
    return GenerateJwtTokenWithClaims(userId, username, null);
  }

  /// <summary>
  /// Generates JWT token with subscription tier claim for rate limiting
  /// </summary>
  private bool IsSelfHost => (_services.GetService<EditionInfo>() ?? EditionInfo.From(_configuration)).IsSelfHost;

  /// <summary>
  /// The tier to put in the JWT claim and the profile. Operators on the Admin:Emails allowlist are
  /// Admin in both editions: the [Authorize(Roles = "Admin")] routes read the role from this claim,
  /// so on hosted the allowlist used to unlock admin screens in the app while their API calls
  /// still answered 403. Otherwise hosted reads the stored tier. Self-host has no billing, so
  /// every other account is Premium; the stored tier is null or a lapsed trial there.
  /// </summary>
  public SubscriptionTier? ResolveTierClaim(User user)
  {
    if (!IsSelfHost)
      return AdminPolicy.IsAdminEmail(_configuration, user.Email) ? SubscriptionTier.Admin : user.SubscriptionTier;
    return user.IsInstanceAdmin || AdminPolicy.IsAdminEmail(_configuration, user.Email)
      ? SubscriptionTier.Admin
      : SubscriptionTier.Premium;
  }

  public string GenerateJwtTokenWithClaims(int userId, string username, SubscriptionTier? subscriptionTier)
  {
    var jwtSettings = _configuration.GetSection("Jwt");
    var key = Encoding.ASCII.GetBytes(jwtSettings["Key"] ?? throw new InvalidOperationException("JWT key not configured"));
    
    var claims = new List<Claim>
    {
      new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
      new Claim(ClaimTypes.Name, username)
    };
    
    // Add subscription tier claim for rate limiting
    if (subscriptionTier.HasValue)
    {
      claims.Add(new Claim("subscription_tier", subscriptionTier.Value.ToString()));
    }
    
    var tokenDescriptor = new SecurityTokenDescriptor
    {
      Subject = new ClaimsIdentity(claims),
      Expires = AccessTokenExpiry(),
      SigningCredentials = new SigningCredentials(
        new SymmetricSecurityKey(key),
        SecurityAlgorithms.HmacSha256Signature),
      Issuer = jwtSettings["Issuer"],
      Audience = jwtSettings["Audience"]
    };

    var tokenHandler = new JwtSecurityTokenHandler();
    var token = tokenHandler.CreateToken(tokenDescriptor);

    return tokenHandler.WriteToken(token);
  }

  /// <summary>
  /// Access-token expiry. Short-lived (Jwt:ExpiryInMinutes, default 60) now that a
  /// rotating refresh token carries the long-lived session — a leaked access token is
  /// only valid for this window. Access-token ExpiresAt values are derived from here.
  /// </summary>
  private DateTime AccessTokenExpiry()
  {
    var minutes = int.TryParse(_configuration["Jwt:ExpiryInMinutes"], out var m) && m > 0 ? m : 60;
    return DateTime.UtcNow.AddMinutes(minutes);
  }

  /// <summary>
  /// Rotates the presented refresh token and issues a fresh access token + refresh
  /// token for the same user. Returns null if the refresh token is invalid, expired,
  /// revoked, or reused (reuse revokes the whole family).
  /// </summary>
  public async Task<LoginResponse?> RefreshTokenAsync(string refreshToken)
  {
    var rotated = await _refreshTokenService.RotateAsync(refreshToken);
    if (rotated == null)
      return null;

    var user = await _userRepository.GetByIdAsync(rotated.UserId);
    if (user == null || user.IsDeleted)
      return null;

    var accessToken = GenerateJwtTokenWithClaims(user.Id, user.Username, ResolveTierClaim(user));

    return new LoginResponse
    {
      Token = accessToken,
      User = BuildProfileDto(user),
      ExpiresAt = AccessTokenExpiry(),
      RefreshToken = rotated.Token
    };
  }

  private static bool VerifyPassword(string password, string hash)
  {
    // In a real application, you should use BCrypt or similar
    // For this MVP, we'll use a simple hash comparison
    return BCrypt.Net.BCrypt.Verify(password, hash);
  }

  public static string HashPassword(string password)
  {
    return BCrypt.Net.BCrypt.HashPassword(password);
  }

  // Security helper methods for Google OAuth
  private static bool IsValidEmail(string email)
  {
    if (string.IsNullOrEmpty(email)) return false;
    
    try
    {
      var emailAddress = new System.Net.Mail.MailAddress(email);
      return emailAddress.Address == email;
    }
    catch
    {
      return false;
    }
  }

  private static bool IsSuspiciousEmailDomain(string email)
  {
    if (string.IsNullOrEmpty(email)) return true;
    
    var domain = email.Split('@').LastOrDefault()?.ToLowerInvariant();
    if (string.IsNullOrEmpty(domain)) return true;

    // List of suspicious or temporary email domains
    var suspiciousDomains = new HashSet<string>
    {
      "tempmail.org", "10minutemail.com", "guerrillamail.com", 
      "mailinator.com", "yopmail.com", "temp-mail.org"
    };

    return suspiciousDomains.Contains(domain);
  }

  private async Task<int> GetRecentGoogleRegistrationsCount()
  {
    try
    {
      // Basic rate limiting - count Google registrations in the last hour
      var oneHourAgo = DateTime.UtcNow.AddHours(-1);
      
      // This would ideally use a distributed cache (Redis) in production
      // For now, we'll query the database (consider caching this)
      var count = await _userRepository.CountGoogleRegistrationsSinceAsync(oneHourAgo);
      return count;
    }
    catch
    {
      // If we can't get count, allow the registration (fail open)
      return 0;
    }
  }

  private async Task ClearDeletedUserIdentifiersAsync(User deletedUser)
  {
    var guid = Guid.NewGuid().ToString("N");
    _logger.LogInformation("Clearing identifiers from deleted user {UserId} for re-registration", deletedUser.Id);
    deletedUser.Email = $"deleted_{deletedUser.Id}_{guid}@deleted.buoyai.local";
    deletedUser.GoogleId = null;
    deletedUser.AppleId = null; // Otherwise the stale AppleId locks the user out of re-registration.
    // Single sign-on links point at the old account; drop them so the person can start fresh.
    var links = await _context.UserExternalLogins.Where(l => l.UserId == deletedUser.Id).ToListAsync();
    if (links.Count > 0) _context.UserExternalLogins.RemoveRange(links);
    deletedUser.Username = $"deleted_{deletedUser.Id}_{guid}";
    await _userRepository.UpdateAsync(deletedUser);
  }

  private static string SanitizeInput(string? input)
  {
    if (string.IsNullOrEmpty(input)) return string.Empty;
    
    // Remove potentially dangerous characters and limit length
    return input.Trim()
                .Replace("<", "")
                .Replace(">", "")
                .Replace("'", "")
                .Replace("\"", "")
                .Replace("&", "")
                .Substring(0, Math.Min(input.Length, 50)); // Max 50 characters
  }

  private async Task<string> GenerateUniqueUsername(string email)
  {
    var baseUsername = email.Split('@')[0].ToLowerInvariant();
    baseUsername = SanitizeInput(baseUsername);
    
    // Check if username exists
    var existingUser = await _userRepository.GetByUsernameAsync(baseUsername);
    if (existingUser == null) return baseUsername;
    
    // Add random suffix if username exists
    var random = new Random();
    for (int i = 0; i < 10; i++) // Try up to 10 times
    {
      var suffix = random.Next(1000, 9999);
      var newUsername = $"{baseUsername}{suffix}";
      
      existingUser = await _userRepository.GetByUsernameAsync(newUsername);
      if (existingUser == null) return newUsername;
    }
    
    // Fallback - use current timestamp
    return $"{baseUsername}{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
  }

  private async Task TriggerSyncIfNeeded(User user)
  {
    try
    {
      // Skip background sync during testing to avoid DbContext concurrency issues
      if (_environment.EnvironmentName == "Testing")
      {
        _logger.LogDebug("Skipping background sync for user {UserId} in testing environment", user.Id);
        return;
      }

      // Check if it's a business day - reduce sync frequency on weekends/holidays
      var now = DateTime.UtcNow;
      var isBusinessDay = now.DayOfWeek != DayOfWeek.Saturday && 
                         now.DayOfWeek != DayOfWeek.Sunday;
      
      // Determine user sync strategy based on subscription tier
      var syncStrategy = await GetUserSyncStrategyAsync(user);
      
      if (syncStrategy == SyncStrategy.OnDemand)
      {
        // For free users, check if data is stale 
        // Use longer staleness threshold on weekends (banks don't process)
        var stalenessThreshold = isBusinessDay 
          ? TimeSpan.FromHours(6)   // Normal weekday threshold
          : TimeSpan.FromHours(12); // Extended weekend threshold
          
        var isStale = await _lastSyncTracker.IsDataStale(user.Id, stalenessThreshold);
        
        if (isStale)
        {
          _logger.LogInformation("Triggering background sync for user {UserId} due to stale data", user.Id);
          
          // Trigger sync in background - don't wait for completion to avoid blocking login.
          // CRITICAL: the background task must resolve its services from its OWN DI scope (a fresh
          // DbContext), never the request-scoped _lastSyncTracker captured here,
          // otherwise it runs concurrently with the rest of the login on the SAME scoped DbContext
          // and throws "A second operation was started on this context instance…". Capture only the
          // value-type userId, not the request-scoped User entity.
          var userId = user.Id;
          _ = Task.Run(async () =>
          {
            try
            {
              using var scope = _scopeFactory.CreateScope();
              var plaidService = scope.ServiceProvider.GetRequiredService<IPlaidService>();
              var lastSyncTracker = scope.ServiceProvider.GetRequiredService<ILastSyncTracker>();
              await plaidService.SyncTransactionsAsync(userId);
              await lastSyncTracker.SetLastSyncTime(userId, DateTime.UtcNow);
              _logger.LogInformation("Background sync completed for user {UserId}", userId);
            }
            catch (Exception ex)
            {
              _logger.LogError(ex, "Background sync failed for user {UserId}", userId);
            }
          });
        }
      }
      else if (syncStrategy == SyncStrategy.RealTime)
      {
        // Premium users already have real-time sync via background services
        _logger.LogDebug("User {UserId} has real-time sync enabled", user.Id);
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error checking sync status for user {UserId}", user.Id);
      // Don't throw - login should not fail due to sync issues
    }
  }

  private async Task<SyncStrategy> GetUserSyncStrategyAsync(User user)
  {
    // Check user subscription tier
    // Premium users get real-time sync
    if (await _entitlements.HasPremiumAccessAsync(user.Id))
    {
      return SyncStrategy.RealTime;
    }
    
    // Standard/Trial users get on-demand sync
    return SyncStrategy.OnDemand;
  }

  private static bool IsValidUrl(string? url)
  {
    if (string.IsNullOrEmpty(url)) return false;
    
    return Uri.TryCreate(url, UriKind.Absolute, out var validUrl) && 
           (validUrl.Scheme == Uri.UriSchemeHttp || validUrl.Scheme == Uri.UriSchemeHttps);
  }

  // Password Reset Methods

  public async Task<bool> RequestPasswordResetAsync(string email, string? ipAddress = null, string? userAgent = null)
  {
    try
    {
      var user = await _userRepository.GetByEmailAsync(email);
      if (user == null)
      {
        // IMPORTANT: Don't reveal if email exists (security best practice)
        _logger.LogWarning("Password reset requested for non-existent email: {Email}", email);
        return true; // Return true to prevent email enumeration attacks
      }

      // Generate cryptographically secure token (32 bytes = 256 bits)
      var tokenBytes = RandomNumberGenerator.GetBytes(32);
      var token = Convert.ToBase64String(tokenBytes)
        .Replace("+", "-")   // Make URL-safe
        .Replace("/", "_")   // Make URL-safe
        .TrimEnd('=');       // Remove padding

      // Create reset token record
      var resetToken = new PasswordResetToken
      {
        UserId = user.Id,
        Token = token,
        ExpiresAt = DateTime.UtcNow.AddHours(24), // 24-hour expiry
        IpAddress = ipAddress,
        UserAgent = userAgent,
        CreatedAt = DateTime.UtcNow
      };

      _context.PasswordResetTokens.Add(resetToken);
      await _context.SaveChangesAsync();

      // Generate reset link - use configured URL with safe production default
      var baseUrl = _configuration["App:BaseUrl"];
      if (string.IsNullOrEmpty(baseUrl))
      {
        // In development, default to localhost; in production, require configuration
        baseUrl = _environment.IsDevelopment() 
          ? "http://localhost:3000" 
          : "https://buoy-ai.com";
        
        if (!_environment.IsDevelopment())
        {
          _logger.LogWarning("App:BaseUrl not configured - using default {BaseUrl}. Configure this in production!", baseUrl);
        }
      }
      var resetLink = $"{baseUrl}/app/reset-password?token={token}";

      // Send email using existing email service
      await _emailService.SendPasswordResetEmailAsync(user.Email, resetLink);
      // Count only genuine resets (real account) — the non-existent-email path
      // early-returns above, so enumeration probes don't inflate this.
      _metrics.RecordPasswordReset();

      _logger.LogInformation("Password reset email sent for user {UserId} from IP {IP}",
        user.Id, ipAddress);
      return true;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error requesting password reset for {Email}", email);
      return false;
    }
  }

  public async Task<bool> ValidatePasswordResetTokenAsync(string token)
  {
    if (string.IsNullOrWhiteSpace(token))
      return false;

    var resetToken = await _context.PasswordResetTokens
      .Include(t => t.User)
      .FirstOrDefaultAsync(t => t.Token == token);

    return resetToken?.IsValid ?? false;
  }

  public async Task<bool> ResetPasswordAsync(string token, string newPassword)
  {
    try
    {
      // Find and validate token
      var resetToken = await _context.PasswordResetTokens
        .Include(t => t.User)
        .FirstOrDefaultAsync(t => t.Token == token);

      if (resetToken == null || !resetToken.IsValid)
      {
        _logger.LogWarning("Invalid or expired password reset token used");
        return false;
      }

      // Validate password strength using existing validator
      var passwordValidation = PasswordValidationService.ValidatePassword(newPassword);
      if (!passwordValidation.IsValid)
      {
        _logger.LogWarning("Weak password attempted in password reset. Errors: {Errors}", 
          string.Join(", ", passwordValidation.Errors));
        return false;
      }

      // Update user password
      var user = resetToken.User;
      user.PasswordHash = HashPassword(newPassword);
      user.UpdatedAt = DateTime.UtcNow;

      // Mark this token as used
      resetToken.UsedAt = DateTime.UtcNow;

      // Invalidate every other outstanding reset token for this user so a second
      // leaked/emailed reset link can't be replayed after the password changed.
      var now = DateTime.UtcNow;
      var otherTokens = await _context.PasswordResetTokens
        .Where(t => t.UserId == user.Id && t.Id != resetToken.Id && t.UsedAt == null)
        .ToListAsync();
      foreach (var otherToken in otherTokens)
      {
        otherToken.UsedAt = now;
      }

      await _context.SaveChangesAsync();

      // Revoke the user's entire refresh-token family: a password reset must log out
      // every existing session (an attacker who still holds a stolen session loses it).
      var revokedSessions = await _refreshTokenService.RevokeAllForUserAsync(user.Id);

      _logger.LogInformation(
        "Password successfully reset for user {UserId}; revoked {SessionCount} session(s) and {TokenCount} other reset token(s)",
        user.Id, revokedSessions, otherTokens.Count);

      return true;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error resetting password with token");
      return false;
    }
  }

  #region Device Trust & Enhanced Auth Methods

  public async Task<UserProfileDto?> ValidateCredentialsAsync(LoginRequest request)
  {
    try
    {
      var user = await _userRepository.GetByEmailAsync(request.Email);
      if (user == null)
      {
        _logger.LogWarning("Validation attempt with non-existent email: {Email}", request.Email);
        return null;
      }

      if (!VerifyPassword(request.Password, user.PasswordHash))
      {
        _logger.LogWarning("Invalid password for user: {Email}", request.Email);
        return null;
      }

      return BuildProfileDto(user);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error validating credentials for email: {Email}", request.Email);
      return null;
    }
  }

  public async Task<UserProfileDto?> GetUserByEmailAsync(string email)
  {
    try
    {
      var user = await _userRepository.GetByEmailAsync(email);
      if (user == null)
        return null;

      return BuildProfileDto(user);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting user by email: {Email}", email);
      return null;
    }
  }

  public Task<string> GenerateMfaTokenAsync(int userId, string mfaMethod = "unknown")
  {
    try
    {
      // Generate a temporary token that expires in 5 minutes
      var tokenHandler = new JwtSecurityTokenHandler();
      var key = Encoding.ASCII.GetBytes(_configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT key not configured"));
      
      var tokenDescriptor = new SecurityTokenDescriptor
      {
        Subject = new ClaimsIdentity(new[]
        {
          new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
          new Claim("type", "mfa_pending")
        }),
        Expires = DateTime.UtcNow.AddMinutes(5), // Short expiration for MFA tokens
        Issuer = _configuration["Jwt:Issuer"],
        Audience = _configuration["Jwt:Audience"],
        SigningCredentials = new SigningCredentials(
          new SymmetricSecurityKey(key), 
          SecurityAlgorithms.HmacSha256Signature)
      };
      
      var token = tokenHandler.CreateToken(tokenDescriptor);
      // An MFA challenge was issued to the user (login required a second factor).
      _metrics.RecordMfaChallenge(mfaMethod);
      return Task.FromResult(tokenHandler.WriteToken(token));
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error generating MFA token for user {UserId}", userId);
      throw;
    }
  }

  /// <summary>
  /// The <see cref="TokenValidationParameters"/> a pending-MFA token must satisfy:
  /// signed with our JWT key, issued/audienced by us, and unexpired. Shared by the
  /// MFA-completion and code-send paths so both enforce the same guarantees.
  /// </summary>
  private TokenValidationParameters MfaTokenValidationParameters()
  {
    var key = Encoding.ASCII.GetBytes(_configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT key not configured"));
    return new TokenValidationParameters
    {
      ValidateIssuerSigningKey = true,
      IssuerSigningKey = new SymmetricSecurityKey(key),
      ValidateIssuer = true,
      ValidIssuer = _configuration["Jwt:Issuer"],
      ValidateAudience = true,
      ValidAudience = _configuration["Jwt:Audience"],
      ValidateLifetime = true,
      ClockSkew = TimeSpan.Zero
    };
  }

  public int? ValidateMfaToken(string mfaToken)
  {
    if (string.IsNullOrEmpty(mfaToken))
      return null;

    try
    {
      var tokenHandler = new JwtSecurityTokenHandler();
      var principal = tokenHandler.ValidateToken(mfaToken, MfaTokenValidationParameters(), out _);

      var userIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
      var typeClaim = principal.FindFirst("type")?.Value;

      if (string.IsNullOrEmpty(userIdClaim) || typeClaim != "mfa_pending"
          || !int.TryParse(userIdClaim, out var userId))
      {
        _logger.LogWarning("Invalid MFA token structure - missing or invalid claims");
        return null;
      }

      return userId;
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "MFA token validation failed");
      return null;
    }
  }

  public async Task<LoginResponse?> CompleteMfaLoginAsync(string mfaToken, string mfaCode)
  {
    _metrics.RecordLoginAttempt();
    try
    {
      _logger.LogInformation("CompleteMfaLoginAsync called");

      // Validate the MFA token's signature/issuer/audience/lifetime + mfa_pending claim.
      var validatedUserId = ValidateMfaToken(mfaToken);
      if (validatedUserId == null)
        return null;

      var userId = validatedUserId.Value;

      // Development fallback: Accept "000000" as a valid code in Development environment
      // This bypasses ALL MFA types (TOTP, Email, etc.) for easier local testing
      var isDevelopment = _environment.IsDevelopment();
      var isDevFallbackCode = isDevelopment && mfaCode == "000000";
      
      _logger.LogInformation("MFA verification - IsDevelopment: {IsDev}, IsDevFallback: {IsDevFallback}",
        isDevelopment, isDevFallbackCode);
      
      if (isDevFallbackCode)
      {
        _logger.LogWarning("⚠️ DEVELOPMENT ONLY: MFA bypassed with fallback code '000000' for user {UserId}. This should NEVER happen in production!", userId);
        // Skip all validation - just return success for development
        var devUser = await _userRepository.GetByIdAsync(userId);
        if (devUser == null)
        {
          _logger.LogWarning("User not found after MFA bypass: {UserId}", userId);
          return null;
        }
        if (devUser.IsDeleted)
        {
          _logger.LogWarning("MFA login attempt for deleted user: {UserId}", userId);
          _metrics.RecordLoginFailure("deleted_account");
          return null;
        }
        var devToken = GenerateJwtTokenWithClaims(devUser.Id, devUser.Username, ResolveTierClaim(devUser));
        var devExpiresAt = AccessTokenExpiry();
        await TriggerSyncIfNeeded(devUser);
        return new LoginResponse
        {
          Token = devToken,
          User = BuildProfileDto(devUser),
          ExpiresAt = devExpiresAt
        };
      }
      
      // Try verification methods in order: email code, TOTP, recovery code
      var mfaUser = await _userRepository.GetByIdAsync(userId);
      if (mfaUser == null) return null;

      var isValid = false;

      // 1. Try email codes from database
      var emailCode = await _context.TwoFactorCodes
        .FirstOrDefaultAsync(c => c.UserId == userId &&
                      c.Code == mfaCode &&
                      !c.IsUsed &&
                      c.ExpiresAt > DateTime.UtcNow &&
                      c.Purpose == "login_verification");
      if (emailCode != null)
      {
        isValid = true;
        emailCode.IsUsed = true;
        emailCode.UsedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
      }

      // 2. Try TOTP verification if user has it configured
      if (!isValid && mfaUser.MfaType == MfaType.Totp && !string.IsNullOrEmpty(mfaUser.TotpSecret))
      {
        try
        {
          var secretBytes = Base32Encoding.ToBytes(MfaSecretProtector.Reveal(_encryptionService, mfaUser.TotpSecret));
          var totp = new Totp(secretBytes);
          var window = new VerificationWindow(previous: 1, future: 1);
          isValid = totp.VerifyTotp(mfaCode, out _, window);
        }
        catch (Exception ex)
        {
          _logger.LogWarning(ex, "TOTP verification error for user {UserId}", userId);
        }
      }

      // 3. Try recovery codes as last resort
      if (!isValid && !string.IsNullOrEmpty(mfaUser.RecoveryCodes))
      {
        List<string> codes;
        try
        {
          codes = MfaSecretProtector.Reveal(_encryptionService, mfaUser.RecoveryCodes).Split(';').ToList();
        }
        catch (Exception ex)
        {
          _logger.LogWarning(ex, "Recovery codes unreadable for user {UserId}", userId);
          codes = new List<string>();
        }
        if (codes.Contains(mfaCode))
        {
          isValid = true;
          codes.Remove(mfaCode);
          mfaUser.RecoveryCodes = codes.Count > 0 ? MfaSecretProtector.Protect(_encryptionService, string.Join(";", codes)) : null;
          mfaUser.UpdatedAt = DateTime.UtcNow;
          await _userRepository.UpdateAsync(mfaUser);
          _logger.LogInformation("Recovery code used for user {UserId}, {Remaining} codes remaining", userId, codes.Count);
        }
      }

      if (!isValid)
      {
        _logger.LogWarning("All MFA verification methods failed for user {UserId}", userId);
        return null;
      }

      if (mfaUser.IsDeleted)
      {
        _logger.LogWarning("MFA login attempt for deleted user: {UserId}", userId);
        _metrics.RecordLoginFailure("deleted_account");
        return null;
      }

      var token = GenerateJwtTokenWithClaims(mfaUser.Id, mfaUser.Username, ResolveTierClaim(mfaUser));
      var expiresAt = AccessTokenExpiry();

      await TriggerSyncIfNeeded(mfaUser);

      _metrics.RecordLoginSuccess();
      return new LoginResponse
      {
        Token = token,
        User = BuildProfileDto(mfaUser),
        ExpiresAt = expiresAt
      };
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error completing MFA login");
      _metrics.RecordLoginFailure("mfa_exception");
      return null;
    }
  }

  #endregion

  #region Trial Invitation

  /// <summary>
  /// The one email a new account gets on day 0. With a trial, that is the trial welcome, whose
  /// single button is "Connect my bank"; without one (a returning email that already used its
  /// trial), the plain welcome. It used to be both at once, and they contradicted each other:
  /// one said to add a payment method to start the trial, the other that the trial had already
  /// started with no card.
  /// </summary>
  private async Task SendFirstEmailAsync(User user, int trialDays)
  {
    if (trialDays > 0)
    {
      await SendTrialInvitationAsync(user, trialDays);
    }
    else
    {
      await SendWelcomeEmailAsync(user);
    }
  }

  /// <summary>
  /// Sends the welcome email to a newly registered user who was not granted a trial.
  ///
  /// Unconditional and deliberately NOT preference-gated: <see cref="UserEmailPreferences"/>
  /// excludes welcome from the opt-out set because it is the transactional confirmation that
  /// an account now exists. When a trial was granted, the trial invitation below plays that
  /// role instead (see <see cref="SendFirstEmailAsync"/>), so nobody gets two welcomes.
  ///
  /// Fail-open: registration must never fail because an email did not send.
  /// </summary>
  private async Task SendWelcomeEmailAsync(User user)
  {
    try
    {
      var firstName = user.FirstName ?? user.Username ?? "there";
      await _emailService.SendWelcomeEmailAsync(user.Email, firstName);
      _logger.LogInformation("Sent welcome email to new user {UserId}", user.Id);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to send welcome email to user {UserId}", user.Id);
    }
  }

  /// <summary>
  /// Sends trial invitation email and creates in-app notification for new users.
  /// </summary>
  private async Task SendTrialInvitationAsync(User user, int trialDays)
  {
    var firstName = user.FirstName ?? user.Username ?? "there";

    // Send trial invitation email
    try
    {
      await _emailService.SendTrialInvitationAsync(user.Email, firstName, trialDays);
      _logger.LogInformation("Sent trial invitation email to new user {UserId}", user.Id);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to send trial invitation email to user {UserId}", user.Id);
      // Don't fail registration if email fails
    }

    // Create in-app notification
    try
    {
      await _notificationService.CreateNotificationAsync(user.Id, new CreateNotificationRequest
      {
        Type = "trial_started",
        Title = "Welcome to Buoy Money",
        Message = $"Your {trialDays}-day free trial is on, no card needed. Connect your bank to get started: it takes about two minutes.",
        ActionUrl = "/accounts",
        ActionText = "Connect my bank"
      });
      _logger.LogInformation("Created trial invitation notification for user {UserId}", user.Id);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to create trial invitation notification for user {UserId}", user.Id);
      // Don't fail registration if notification fails
    }
  }

  #endregion

  #region Account Deletion

  /// <summary>
  /// Permanently deletes a user account and all associated data.
  /// This operation is irreversible.
  /// </summary>
  public async Task<bool> DeleteAccountAsync(int userId, string? password = null)
  {
    try
    {
      var user = await _userRepository.GetByIdAsync(userId);
      if (user == null)
      {
        _logger.LogWarning("Account deletion attempted for non-existent user {UserId}", userId);
        return false;
      }

      // For non-Google users, verify password if provided
      if (!string.IsNullOrEmpty(user.PasswordHash) && !string.IsNullOrEmpty(password))
      {
        if (!VerifyPassword(password, user.PasswordHash))
        {
          _logger.LogWarning("Account deletion failed - invalid password for user {UserId}", userId);
          return false;
        }
      }

      _logger.LogInformation("Starting account deletion for user {UserId} ({Email})", userId, user.Email);

      // 1. Cancel any active subscription (also strips PremiumInherited from family members)
      // Billing is resolved here and nowhere else: its Stripe client throws at construction
      // without keys, and sign-in must not depend on it. No billing means no Stripe state
      // to clean up, so those steps are skipped and the rest of the deletion proceeds.
      var billing = TryResolveBilling(userId);

      try
      {
        if (billing != null)
        {
          await billing.CancelSubscriptionAsync(userId, new Core.DTOs.Billing.CancelSubscriptionRequest { CancelImmediately = true });
          _logger.LogInformation("Cancelled subscription for user {UserId}", userId);
        }
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Failed to cancel subscription for user {UserId} during account deletion", userId);
      }

      // 1b. Delete the Stripe customer object entirely (name/email/payment history). Cancelling
      //     the subscription above does NOT remove the customer, so their PII would otherwise
      //     persist at Stripe after deletion — a data-retention gap. Best-effort.
      try
      {
        if (billing != null)
        await billing.DeleteStripeCustomerAsync(userId);
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Failed to delete Stripe customer for user {UserId} during account deletion", userId);
      }

      // 1c. Revoke the Sign-in-with-Apple grant at Apple (App Store account-deletion
      //     requirement) using the refresh token captured at login. Best-effort.
      await TryRevokeAppleGrantAsync(user);

      // 2. Dissolve any family the user owns — cascade deletes all FamilyMember and
      //    FamilyInvitation rows, leaving former members' individual accounts intact
      try
      {
        var ownedFamilies = await _context.Families
          .Where(f => f.CreatedByUserId == userId)
          .ToListAsync();
        if (ownedFamilies.Count > 0)
        {
          _context.Families.RemoveRange(ownedFamilies);
          await _context.SaveChangesAsync();
          _logger.LogInformation("Dissolved {Count} family/families owned by deleting user {UserId}", ownedFamilies.Count, userId);
        }
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Failed to dissolve owned family/families for user {UserId} during account deletion", userId);
      }


      // 3. Disconnect all bank connections, whichever provider each came from
      try
      {
        var connections = await _context.PlaidConnections
          .Where(c => c.UserId == userId && c.IsActive)
          .Select(c => new { c.Id, c.Provider })
          .ToListAsync();
        foreach (var connection in connections)
        {
          try
          {
            if (!_aggregators.Value.TryGetForLifecycle(connection.Provider, out var aggregator))
            {
              throw new NotSupportedException($"No bank aggregator is registered for provider '{connection.Provider}'.");
            }
            await aggregator!.DisconnectConnectionAsync(userId, connection.Id);
            _logger.LogInformation("Disconnected {Provider} connection {ConnectionId} for user {UserId}", connection.Provider, connection.Id, userId);
          }
          catch (Exception ex)
          {
            _logger.LogWarning(ex, "Failed to disconnect {Provider} connection {ConnectionId} for user {UserId}", connection.Provider, connection.Id, userId);
          }
        }
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Failed to get/disconnect bank connections for user {UserId}", userId);
      }

      // 4. Capture email/name before deletion (user record is anonymized after)
      var userEmail = user.Email;
      var userDisplayName = user.FirstName ?? user.Username;

      // 4b. Remove the user's vectors (chat text, merchant facts). With the pgvector provider these
      //     live in the primary database with no FK to Users, so nothing else cascades them.
      //     Best-effort: a vector store outage must not block account deletion.
      try
      {
        await _services.GetRequiredService<IChatEmbeddingService>().DeleteUserEmbeddingsAsync(userId);
        await _services.GetRequiredService<ITransactionEmbeddingService>().DeleteUserEmbeddingsAsync(userId);
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Failed to delete embeddings for user {UserId} during account deletion", userId);
      }

      // 5. Delete all user data (GDPR/CCPA compliant — anonymizes user record for audit trail)
      await _dataRetentionManager.DeleteUserDataImmediatelyAsync(userId);

      _logger.LogInformation("Successfully deleted account for user {UserId} ({Email})", userId, userEmail);
      _metrics.RecordAccountDeleted();

      // 6. Send confirmation email only after deletion succeeds so we never send
      //    a "your account was deleted" email when deletion actually failed.
      try
      {
        await _emailService.SendAccountDeletionConfirmationAsync(userEmail, userDisplayName);
        _logger.LogInformation("Sent account deletion confirmation email to user {UserId}", userId);
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Failed to send account deletion confirmation email to user {UserId}", userId);
      }

      return true;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to delete account for user {UserId}", userId);
      throw;
    }
  }

  #endregion
}
