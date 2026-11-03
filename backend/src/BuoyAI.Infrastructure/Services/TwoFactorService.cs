using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OtpNet;
using QRCoder;
using static QRCoder.QRCodeGenerator;
using System.Security.Cryptography.X509Certificates;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Service for managing two-factor authentication
/// </summary>
public class TwoFactorService : ITwoFactorService
{
    private readonly BuoyAIDbContext _context;
    private readonly IEmailService _emailService;
    private readonly IUserRepository _userRepository;
    private readonly ILogger<TwoFactorService> _logger;
    private readonly WebAuthnSettings _webAuthnSettings;
    private readonly IEncryptionService _encryption;

    /// <summary>Minimum seconds between sending codes for the same user/purpose.</summary>
    private const int ResendCooldownSeconds = 30;

    public TwoFactorService(
        BuoyAIDbContext context,
        IEmailService emailService,
        IUserRepository userRepository,
        ILogger<TwoFactorService> logger,
        IOptions<WebAuthnSettings> webAuthnSettings,
        IEncryptionService encryption)
    {
        _encryption = encryption;
        _context = context;
        _emailService = emailService;
        _userRepository = userRepository;
        _logger = logger;
        _webAuthnSettings = webAuthnSettings.Value;
    }

    public async Task<string> GenerateAndSendCodeAsync(int userId, string purpose, string? ipAddress = null, string? userAgent = null)
    {
        try
        {
            var user = await _userRepository.GetByIdAsync(userId);
            if (user == null)
                throw new ArgumentException("User not found", nameof(userId));

            // Resend throttle: if a still-valid code was issued very recently, don't send a
            // new email — return the existing code. Prevents inbox flooding / send spam.
            var recent = await _context.TwoFactorCodes
                .Where(c => c.UserId == userId && c.Purpose == purpose && !c.IsUsed && c.ExpiresAt > DateTime.UtcNow)
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefaultAsync();
            if (recent != null && (DateTime.UtcNow - recent.CreatedAt).TotalSeconds < ResendCooldownSeconds)
            {
                _logger.LogInformation(
                    "Throttling 2FA resend for user {UserId}, purpose {Purpose} (recent code still valid)",
                    userId, purpose);
                return recent.Code;
            }

            // Invalidate any existing codes for this purpose
            await InvalidateCodesAsync(userId, purpose);

            // Generate 6-digit code
            var code = GenerateRandomCode();
            var expiresAt = DateTime.UtcNow.AddMinutes(10); // 10-minute expiry

            // Save to database
            var twoFactorCode = new TwoFactorCode
            {
                UserId = userId,
                Code = code,
                Purpose = purpose,
                ExpiresAt = expiresAt,
                CreatedAt = DateTime.UtcNow,
                IpAddress = ipAddress,
                UserAgent = userAgent
            };

            _context.TwoFactorCodes.Add(twoFactorCode);
            await _context.SaveChangesAsync();

            // Send email
            await _emailService.SendTwoFactorCodeAsync(user.Email, code, purpose, ipAddress);

            _logger.LogInformation("2FA code generated and sent for user {UserId}, purpose: {Purpose}", userId, purpose);
            return code;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating 2FA code for user {UserId}", userId);
            throw;
        }
    }

    /// <summary>Maximum wrong guesses before a code is invalidated (anti-brute-force).</summary>
    private const int MaxCodeAttempts = 5;

    public async Task<bool> VerifyCodeAsync(int userId, string code, string purpose)
    {
        try
        {
            // Find the active code for this user/purpose regardless of what was submitted,
            // so we can count wrong guesses against it and lock it after too many.
            var active = await _context.TwoFactorCodes
                .Where(tfc => tfc.UserId == userId &&
                             tfc.Purpose == purpose &&
                             !tfc.IsUsed &&
                             tfc.ExpiresAt > DateTime.UtcNow)
                .OrderByDescending(tfc => tfc.CreatedAt)
                .FirstOrDefaultAsync();

            if (active == null)
            {
                _logger.LogWarning("No active 2FA code for user {UserId}, purpose: {Purpose}", userId, purpose);
                return false;
            }

            if (active.Code != code)
            {
                active.Attempts += 1;
                if (active.Attempts >= MaxCodeAttempts)
                {
                    active.IsUsed = true; // Invalidate — the user must request a new code.
                    _logger.LogWarning(
                        "2FA code for user {UserId} purpose {Purpose} invalidated after {Attempts} failed attempts",
                        userId, purpose, active.Attempts);
                }
                await _context.SaveChangesAsync();
                return false;
            }

            active.IsUsed = true;
            active.UsedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("2FA code verified successfully for user {UserId}, purpose: {Purpose}", userId, purpose);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying 2FA code for user {UserId}", userId);
            return false;
        }
    }

    public async Task InvalidateCodesAsync(int userId, string? purpose = null)
    {
        try
        {
            var query = _context.TwoFactorCodes.Where(tfc => tfc.UserId == userId && !tfc.IsUsed);
            
            if (!string.IsNullOrEmpty(purpose))
                query = query.Where(tfc => tfc.Purpose == purpose);

            var codes = await query.ToListAsync();
            
            foreach (var code in codes)
            {
                code.IsUsed = true;
                code.UsedAt = DateTime.UtcNow;
            }

            if (codes.Any())
            {
                await _context.SaveChangesAsync();
                _logger.LogInformation("Invalidated {Count} 2FA codes for user {UserId}", codes.Count, userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invalidating 2FA codes for user {UserId}", userId);
            throw;
        }
    }

    /// <summary>
    /// How long email-code MFA counts as sufficient for Plaid before a phishing-resistant
    /// method (TOTP/passkey) is required. Measured from <see cref="User.MfaEnabledAt"/>.
    /// </summary>
    public static readonly TimeSpan EmailMfaPlaidWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// True while an email-MFA user is inside <see cref="EmailMfaPlaidWindow"/>.
    /// </summary>
    private static bool IsInsideEmailWindow(User user, DateTime nowUtc) =>
        user.MfaType == MfaType.Email
        && user.MfaEnabledAt.HasValue
        && nowUtc <= user.MfaEnabledAt.Value.Add(EmailMfaPlaidWindow);

    public async Task<bool> RequiresUpgradeAsync(int userId)
    {
        try
        {
            var user = await _userRepository.GetByIdAsync(userId);
            if (user == null) return false;

            // If user has phishing-resistant MFA, no upgrade needed
            if (user.MfaType == MfaType.Totp || user.MfaType == MfaType.Passkey)
                return false;

            // Email MFA: upgrade is required once the window has lapsed
            if (user.MfaType == MfaType.Email && user.MfaEnabledAt.HasValue)
                return !IsInsideEmailWindow(user, DateTime.UtcNow);

            // No MFA setup at all
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking MFA upgrade requirement for user {UserId}", userId);
            return true; // Fail safe - require upgrade if we can't determine
        }
    }

    public async Task<bool> CanAccessPlaidFeaturesAsync(int userId)
    {
        try
        {
            var user = await _userRepository.GetByIdAsync(userId);
            if (user == null) return false;

            // TOTP and Passkey are phishing-resistant: always sufficient.
            if (user.MfaType == MfaType.Totp || user.MfaType == MfaType.Passkey)
                return true;

            // Email-code MFA is admitted for a bounded window (2026-08: 10/10 real-user
            // bank-connect attempts died on the hard TOTP/passkey wall, and most people
            // will not install an authenticator app cold). Verified inbox control gets
            // them started; new/re-linking after the window needs the strong method.
            // Existing connections keep syncing regardless (sync uses the stored Plaid
            // access token, not the user's MFA level).
            return IsInsideEmailWindow(user, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking Plaid access for user {UserId}", userId);
            return false; // Fail safe - deny access if we can't determine
        }
    }

    public async Task EnableEmailMfaAsync(int userId)
    {
        try
        {
            var user = await _userRepository.GetByIdAsync(userId);
            if (user == null)
                throw new ArgumentException("User not found", nameof(userId));

            user.MfaType = MfaType.Email;
            user.HasMfaEnabled = true;
            user.MfaEnabledAt = DateTime.UtcNow;
            user.UpdatedAt = DateTime.UtcNow;

            await _userRepository.UpdateAsync(user);

            _logger.LogInformation("Email MFA enabled for user {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enabling email MFA for user {UserId}", userId);
            throw;
        }
    }

    public async Task<TotpSetupDto> EnableTotpMfaAsync(int userId)
    {
        try
        {
            var user = await _userRepository.GetByIdAsync(userId);
            if (user == null)
                throw new ArgumentException("User not found", nameof(userId));

            // Generate TOTP secret
            var secret = GenerateTotpSecret();
            
            // Store temporary secret (will be permanent after verification)
            user.TotpSecret = MfaSecretProtector.Protect(_encryption, secret);
            user.UpdatedAt = DateTime.UtcNow;
            await _userRepository.UpdateAsync(user);

            // Create minimal TOTP URI manually for smaller QR code
            var issuer = Uri.EscapeDataString(BuoyAI.Core.Configuration.Brand.Name);
            var account = Uri.EscapeDataString(user.Email ?? BuoyAI.Core.Configuration.Brand.Name);
            var minimalUri = $"otpauth://totp/{issuer}:{account}?secret={secret}&issuer={issuer}";
            _logger.LogDebug("Generated TOTP URI for user {Email} (length: {Length})", user.Email, minimalUri.Length);
            
            var qrCodeUrl = GenerateQrCodeDataUrl(minimalUri);
            _logger.LogDebug("Generated QR code data URL, length: {Length}", qrCodeUrl?.Length ?? 0);

            var result = new TotpSetupDto
            {
                Secret = secret,
                QrCodeUrl = qrCodeUrl ?? string.Empty,
                ManualEntryCode = FormatSecretForManualEntry(secret)
            };

            _logger.LogInformation("TOTP setup initiated for user {UserId}, QR code length: {QrLength}", userId, qrCodeUrl?.Length ?? 0);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting up TOTP for user {UserId}", userId);
            throw;
        }
    }

    public async Task<string[]> CompleteTotpSetupAsync(int userId, string verificationCode)
    {
        try
        {
            var user = await _userRepository.GetByIdAsync(userId);
            if (user == null)
                throw new ArgumentException("User not found", nameof(userId));

            if (string.IsNullOrEmpty(user.TotpSecret))
                throw new InvalidOperationException("TOTP setup not initiated");

            // Use Otp.NET library for secure, well-maintained TOTP validation
            var secretBytes = Base32Encoding.ToBytes(MfaSecretProtector.Reveal(_encryption, user.TotpSecret!));
            var totp = new Totp(secretBytes);
            
            // Verify with time window tolerance (±1 step = ±30 seconds for clock drift)
            var verificationWindow = new VerificationWindow(previous: 1, future: 1);
            var isValid = totp.VerifyTotp(verificationCode, out long timeUsed, verificationWindow);
            
            if (!isValid)
            {
                throw new InvalidOperationException("Invalid verification code");
            }

            // Complete TOTP setup
            user.MfaType = MfaType.Totp;
            user.HasMfaEnabled = true;
            user.MfaEnabledAt = DateTime.UtcNow;
            user.UpdatedAt = DateTime.UtcNow;

            // Generate recovery codes
            var recoveryCodes = GenerateRecoveryCodes();
            user.RecoveryCodes = MfaSecretProtector.Protect(_encryption, string.Join(";", recoveryCodes));

            await _userRepository.UpdateAsync(user);

            _logger.LogInformation("TOTP setup completed for user {UserId}", userId);
            return recoveryCodes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing TOTP setup for user {UserId}", userId);
            throw;
        }
    }

    public async Task<string[]> GenerateRecoveryCodesAsync(int userId)
    {
        try
        {
            var user = await _userRepository.GetByIdAsync(userId);
            if (user == null)
                throw new ArgumentException("User not found", nameof(userId));

            var recoveryCodes = GenerateRecoveryCodes();
            user.RecoveryCodes = MfaSecretProtector.Protect(_encryption, string.Join(";", recoveryCodes));
            user.UpdatedAt = DateTime.UtcNow;

            await _userRepository.UpdateAsync(user);

            _logger.LogInformation("Recovery codes regenerated for user {UserId}", userId);
            return recoveryCodes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating recovery codes for user {UserId}", userId);
            throw;
        }
    }

    public async Task DisableMfaAsync(int userId)
    {
        var user = await _userRepository.GetByIdAsync(userId);
        if (user == null)
            throw new ArgumentException("User not found", nameof(userId));

        // Nothing to disable — treat as a client error so the caller shows a clear message
        // instead of silently "succeeding" on an account that had no MFA.
        if (!user.HasMfaEnabled && !user.HasPasskeyRegistered)
            throw new InvalidOperationException("MFA is not currently enabled.");

        // Clear every configured factor. This endpoint is the "Disable ALL MFA" action, so
        // it strips TOTP, the MFA flags/grace period, recovery codes, and all passkeys.
        user.HasMfaEnabled = false;
        user.MfaType = MfaType.None;
        user.TotpSecret = null;
        user.MfaEnabledAt = null;
        user.MfaGracePeriodEndsAt = null;
        user.RecoveryCodes = null;
        user.HasPasskeyRegistered = false;
        user.UpdatedAt = DateTime.UtcNow;
        await _userRepository.UpdateAsync(user);

        // Remove registered passkeys and invalidate any outstanding email codes so no stale
        // factor survives the disable.
        var passkeys = await _context.UserPasskeys.Where(p => p.UserId == userId).ToListAsync();
        if (passkeys.Count > 0)
        {
            _context.UserPasskeys.RemoveRange(passkeys);
            await _context.SaveChangesAsync();
        }

        await InvalidateCodesAsync(userId);

        _logger.LogInformation(
            "MFA disabled for user {UserId} ({PasskeyCount} passkeys removed)", userId, passkeys.Count);
    }

    #region Passkey Methods

    public async Task<PasskeyRegistrationOptionsDto> InitiatePasskeyRegistrationAsync(int userId, PasskeyRegistrationInitDto request)
    {
        try
        {
            var user = await _userRepository.GetByIdAsync(userId);
            if (user == null)
                throw new ArgumentException("User not found", nameof(userId));

            // Generate cryptographically secure challenge
            var challengeBytes = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(challengeBytes);
            var challenge = Convert.ToBase64String(challengeBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            // Create proper WebAuthn credential creation options according to W3C spec
            var options = new
            {
                challenge = challenge,
                rp = new 
                { 
                    name = _webAuthnSettings.RelyingPartyName,
                    id = _webAuthnSettings.RelyingPartyId 
                },
                user = new
                {
                    id = Convert.ToBase64String(Encoding.UTF8.GetBytes($"user_{userId}")).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
                    name = user.Email,
                    displayName = request.DisplayName ?? user.Email
                },
                pubKeyCredParams = new[]
                {
                    new { alg = -7, type = "public-key" },   // ES256 (ECDSA w/ SHA-256)
                    new { alg = -257, type = "public-key" }  // RS256 (RSASSA-PKCS1-v1_5 w/ SHA-256)
                    // Only what WebAuthnVerifier can check; ES384/ES512 would pass the ceremony and then be refused.
                },
                authenticatorSelection = new
                {
                    authenticatorAttachment = _webAuthnSettings.AuthenticatorAttachment,
                    userVerification = _webAuthnSettings.UserVerification,
                    requireResidentKey = _webAuthnSettings.RequireResidentKey,
                    residentKey = _webAuthnSettings.RequireResidentKey ? "required" : "preferred"
                },
                timeout = _webAuthnSettings.Timeout,
                attestation = _webAuthnSettings.AttestationConveyancePreference,
                excludeCredentials = await GetExistingCredentialsAsync(userId) // Prevent duplicate registrations
            };

            var optionsJson = JsonSerializer.Serialize(options, new JsonSerializerOptions 
            { 
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
            });

            // Clean up any existing unused registration challenges for this user
            // This prevents the "stale challenge" issue when users restart the flow
            var existingChallenges = await _context.PasskeyChallenges
                .Where(c => c.UserId == userId && c.Type == "registration" && !c.IsUsed)
                .ToListAsync();
            
            if (existingChallenges.Any())
            {
                _logger.LogInformation("Cleaning up {Count} unused registration challenges for user {UserId}", 
                    existingChallenges.Count, userId);
                _context.PasskeyChallenges.RemoveRange(existingChallenges);
            }

            // Store challenge and options in database for verification
            var challengeEntity = new PasskeyChallenge
            {
                UserId = userId,
                Challenge = challenge,
                Options = optionsJson,
                Type = "registration",
                ExpiresAt = DateTime.UtcNow.AddMinutes(10),
                CreatedAt = DateTime.UtcNow,
                IsUsed = false
            };

            _context.PasskeyChallenges.Add(challengeEntity);
            await _context.SaveChangesAsync();

            var result = new PasskeyRegistrationOptionsDto
            {
                Challenge = challenge,
                CredentialCreationOptions = optionsJson
            };

            _logger.LogInformation("Secure WebAuthn passkey registration initiated for user {UserId}", userId);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initiating passkey registration for user {UserId}", userId);
            throw;
        }
    }

    public async Task CompletePasskeyRegistrationAsync(int userId, PasskeyRegistrationCompleteDto request)
    {
        try
        {
            var user = await _userRepository.GetByIdAsync(userId);
            if (user == null)
                throw new ArgumentException("User not found", nameof(userId));

            // Find and validate stored challenge
            var storedChallenge = await _context.PasskeyChallenges
                .FirstOrDefaultAsync(c => c.UserId == userId && 
                                         c.Type == "registration" && 
                                         !c.IsUsed && 
                                         c.ExpiresAt > DateTime.UtcNow);

            if (storedChallenge == null)
            {
                // Check if there are expired challenges to provide better error message
                var hasExpiredChallenges = await _context.PasskeyChallenges
                    .AnyAsync(c => c.UserId == userId && 
                                  c.Type == "registration" && 
                                  !c.IsUsed && 
                                  c.ExpiresAt <= DateTime.UtcNow);
                
                if (hasExpiredChallenges)
                {
                    _logger.LogWarning("User {UserId} attempted to complete registration with expired challenge", userId);
                    throw new InvalidOperationException("Registration challenge has expired. Please start the passkey setup again.");
                }
                
                throw new InvalidOperationException("No valid registration challenge found. Please start the passkey setup again.");
            }

            // Parse and validate the credential response
            var credentialResponse = JsonSerializer.Deserialize<JsonElement>(request.CredentialResponse);
            
            // Validate credential response structure
            if (!credentialResponse.TryGetProperty("id", out var credentialIdElement) ||
                !credentialResponse.TryGetProperty("response", out var responseElement) ||
                !credentialResponse.TryGetProperty("type", out var typeElement))
            {
                throw new InvalidOperationException("Invalid credential response structure");
            }

            var credentialId = credentialIdElement.GetString();
            if (string.IsNullOrEmpty(credentialId) || typeElement.GetString() != "public-key")
                throw new InvalidOperationException("Invalid credential data");

            // Validate attestation response
            if (!responseElement.TryGetProperty("attestationObject", out var attestationElement) ||
                !responseElement.TryGetProperty("clientDataJSON", out var clientDataElement))
            {
                throw new InvalidOperationException("Missing attestation data");
            }

            var attestationObjectB64 = attestationElement.GetString();
            var clientDataJSONB64 = clientDataElement.GetString();

            if (string.IsNullOrEmpty(attestationObjectB64) || string.IsNullOrEmpty(clientDataJSONB64))
                throw new InvalidOperationException("Invalid attestation or client data");

            // Decode and validate client data
            var clientDataBytes = Convert.FromBase64String(PadBase64String(clientDataJSONB64));
            var clientDataJson = Encoding.UTF8.GetString(clientDataBytes);
            var clientData = JsonSerializer.Deserialize<JsonElement>(clientDataJson);

            // Verify client data
            if (!clientData.TryGetProperty("type", out var clientType) || 
                clientType.GetString() != "webauthn.create" ||
                !clientData.TryGetProperty("challenge", out var clientChallenge) ||
                clientChallenge.GetString() != storedChallenge.Challenge)
            {
                throw new InvalidOperationException("Client data validation failed");
            }

            RequireAllowedOrigin(clientData);

            // Read the credential's real public key (a COSE key), checking it was made for this site
            // with the user present. Sign-in verifies every assertion's signature against it.
            byte[] coseKey;
            try
            {
                coseKey = WebAuthnVerifier.ReadCredentialPublicKey(
                    Convert.FromBase64String(PadBase64String(attestationObjectB64)),
                    _webAuthnSettings.RelyingPartyId,
                    _webAuthnSettings.RequiresUserVerification);
            }
            catch (Exception ex) when (ex is FormatException or System.Formats.Cbor.CborContentException)
            {
                throw new InvalidOperationException("Failed to read the passkey's public key", ex);
            }
            var publicKeyData = Convert.ToBase64String(coseKey);

            // Check for duplicate credential (tolerate any base64/base64url encoding)
            var registrationVariants = CredentialIdVariants(credentialId);
            var existingCredential = await _context.UserPasskeys
                .FirstOrDefaultAsync(p => registrationVariants.Contains(p.CredentialId) && p.IsActive);

            if (existingCredential != null)
                throw new InvalidOperationException("Credential already registered");

            // Store the credential with real public key (canonical unpadded base64url)
            var passkey = new UserPasskey
            {
                UserId = userId,
                CredentialId = NormalizeCredentialId(credentialId),
                PublicKey = publicKeyData,
                Name = request.DeviceName ?? "Passkey",
                DeviceType = DetermineDeviceType(request.DeviceName),
                SignCount = 0,
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            };

            _context.UserPasskeys.Add(passkey);

            // Mark challenge as used
            storedChallenge.IsUsed = true;
            // UsedAt property not available - just mark as used

            // Update user MFA settings if this is their first passkey
            if (!user.HasPasskeyRegistered)
            {
                user.HasPasskeyRegistered = true;
                user.MfaType = MfaType.Passkey;
                user.HasMfaEnabled = true;
                user.MfaEnabledAt = DateTime.UtcNow;
                user.UpdatedAt = DateTime.UtcNow;
                await _userRepository.UpdateAsync(user);
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("Secure passkey registration completed for user {UserId} with credential {CredentialId}", 
                userId, credentialId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing passkey registration for user {UserId}", userId);
            throw;
        }
    }

    public async Task<PasskeyAuthenticationOptionsDto> InitiatePasskeyAuthenticationAsync(PasskeyAuthenticationInitDto request)
    {
        try
        {
            // Generate cryptographically secure challenge
            var challengeBytes = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(challengeBytes);
            var challenge = Convert.ToBase64String(challengeBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            // When a specific user is known (e.g. step-up MFA), scope the ceremony to
            // that user's registered credentials. Otherwise leave allowCredentials empty
            // for discoverable-credential (passwordless) login. An empty list lets the
            // platform authenticator return ANY synced passkey for the relying party,
            // which can be one we never stored — causing "Credential not found".
            object[] allowCredentials = Array.Empty<object>();
            if (request.UserId.HasValue)
            {
                var userCredentialIds = await _context.UserPasskeys
                    .Where(p => p.UserId == request.UserId.Value && p.IsActive)
                    .Select(p => p.CredentialId)
                    .ToListAsync();

                // NOTE: each entry MUST include "transports" (even if empty). The Flutter
                // "passkeys" plugin deserializes allowCredentials via a generated
                // CredentialType.fromJson that does `(json['transports'] as List)` with no
                // null check, so omitting the field throws during request parsing — the
                // authenticator is never invoked and the user sees "verification failed"
                // with no biometric prompt.
                allowCredentials = userCredentialIds
                    .Select(id => (object)new { type = "public-key", id, transports = Array.Empty<string>() })
                    .ToArray();
            }

            // Create proper WebAuthn assertion options for authentication
            var options = new
            {
                challenge = challenge,
                timeout = _webAuthnSettings.Timeout,
                rpId = _webAuthnSettings.RelyingPartyId,
                allowCredentials = allowCredentials,
                userVerification = _webAuthnSettings.UserVerification
            };

            var optionsJson = JsonSerializer.Serialize(options, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            // Store challenge in database for verification
            var challengeEntity = new PasskeyChallenge
            {
                UserId = request.UserId, // Scoped to the user for step-up; null for discoverable login
                Challenge = challenge,
                Options = optionsJson,
                Type = "authentication",
                ExpiresAt = DateTime.UtcNow.AddMinutes(5),
                CreatedAt = DateTime.UtcNow,
                IsUsed = false
            };

            _context.PasskeyChallenges.Add(challengeEntity);
            await _context.SaveChangesAsync();

            var result = new PasskeyAuthenticationOptionsDto
            {
                Challenge = challenge,
                AssertionOptions = optionsJson
            };

            _logger.LogInformation("Secure WebAuthn passkey authentication initiated");
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initiating passkey authentication");
            throw;
        }
    }

    public async Task<User> CompletePasskeyAuthenticationAsync(PasskeyAuthenticationCompleteDto request)
    {
        try
        {
            // Parse and validate assertion response structure
            var assertionResponse = JsonSerializer.Deserialize<JsonElement>(request.AssertionResponse);
            
            if (!assertionResponse.TryGetProperty("id", out var credentialIdElement) ||
                !assertionResponse.TryGetProperty("response", out var responseElement) ||
                !assertionResponse.TryGetProperty("type", out var typeElement))
            {
                throw new InvalidOperationException("Invalid assertion response structure");
            }

            var credentialId = credentialIdElement.GetString();
            if (string.IsNullOrEmpty(credentialId) || typeElement.GetString() != "public-key")
                throw new InvalidOperationException("Invalid credential data");

            // Validate authenticator assertion response
            if (!responseElement.TryGetProperty("authenticatorData", out var authDataElement) ||
                !responseElement.TryGetProperty("clientDataJSON", out var clientDataElement) ||
                !responseElement.TryGetProperty("signature", out var signatureElement))
            {
                throw new InvalidOperationException("Missing assertion response data");
            }

            var authenticatorDataB64 = authDataElement.GetString();
            var clientDataJSONB64 = clientDataElement.GetString();
            var signatureB64 = signatureElement.GetString();

            if (string.IsNullOrEmpty(authenticatorDataB64) || 
                string.IsNullOrEmpty(clientDataJSONB64) || 
                string.IsNullOrEmpty(signatureB64))
            {
                throw new InvalidOperationException("Invalid assertion data");
            }

            // Find the credential and user. The credential id may be base64url- or
            // standard-base64-encoded (padded or not) depending on the platform, so match
            // against every equivalent encoding rather than an exact string comparison.
            var lookupVariants = CredentialIdVariants(credentialId);
            var passkey = await _context.UserPasskeys
                .Include(p => p.User)
                .FirstOrDefaultAsync(p => lookupVariants.Contains(p.CredentialId) && p.IsActive);

            if (passkey == null)
            {
                // Never log other users' credential ids here: anyone can reach this with a made-up id.
                _logger.LogWarning("Passkey credential not found. Incoming id={CredentialId}", credentialId);
                throw new InvalidOperationException("Credential not found or inactive");
            }

            // Decode and validate client data
            var clientDataBytes = Convert.FromBase64String(PadBase64String(clientDataJSONB64));
            var clientDataJson = Encoding.UTF8.GetString(clientDataBytes);
            var clientData = JsonSerializer.Deserialize<JsonElement>(clientDataJson);

            if (!clientData.TryGetProperty("type", out var clientType) ||
                clientType.GetString() != "webauthn.get" ||
                !clientData.TryGetProperty("challenge", out var clientChallenge) ||
                string.IsNullOrEmpty(clientChallenge.GetString()))
            {
                throw new InvalidOperationException("Client data validation failed");
            }

            // The challenge must be one this server issued, unused and unexpired (not just any open one),
            // and a step-up challenge is tied to the signed-in user.
            var challengeValue = clientChallenge.GetString();
            var storedChallenge = await _context.PasskeyChallenges
                .FirstOrDefaultAsync(c => c.Type == "authentication" &&
                                         !c.IsUsed &&
                                         c.ExpiresAt > DateTime.UtcNow &&
                                         c.Challenge == challengeValue);

            if (storedChallenge == null)
                throw new InvalidOperationException("No valid authentication challenge found");
            if (storedChallenge.UserId.HasValue && storedChallenge.UserId.Value != passkey.UserId)
                throw new InvalidOperationException("This passkey belongs to a different account");

            RequireAllowedOrigin(clientData);

            if (!IsSupportedStoredKey(passkey.PublicKey))
            {
                _logger.LogWarning("Passkey {PasskeyId} for user {UserId} has no usable public key (saved before keys were stored properly)",
                    passkey.Id, passkey.UserId);
                throw new InvalidOperationException(
                    "This passkey needs to be set up again. Sign in another way, then remove it and add it again in Settings.");
            }

            // Verify signature using stored public key
            var isValidSignature = VerifyAssertionSignature(
                authenticatorDataB64, 
                clientDataJSONB64, 
                signatureB64, 
                passkey.PublicKey);

            if (!isValidSignature)
                throw new InvalidOperationException("Invalid signature - authentication failed");

            // Parse authenticator data for sign count (basic implementation)
            var authDataBytes = Convert.FromBase64String(PadBase64String(authenticatorDataB64));
            var newSignCount = ExtractSignCount(authDataBytes);

            // Verify sign count is greater than stored count (replay protection)
            if (newSignCount > 0 && newSignCount <= passkey.SignCount)
            {
                _logger.LogWarning("Sign count did not increase for passkey {CredentialId}, possible replay attack", 
                    credentialId);
                // In production, you might want to disable the credential or alert
            }

            // Update passkey usage
            passkey.LastUsedAt = DateTime.UtcNow;
            passkey.SignCount = (int)Math.Min(int.MaxValue, Math.Max((long)passkey.SignCount, newSignCount));

            // Mark the challenge used. IsUsed is a concurrency token, so if a second request redeemed the
            // same challenge first, this save matches no row and the sign-in fails (one use, even at once).
            storedChallenge.IsUsed = true;
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new InvalidOperationException("No valid authentication challenge found");
            }

            _logger.LogInformation("Secure passkey authentication completed for user {UserId} with credential {CredentialId}", 
                passkey.UserId, credentialId);
            
            return passkey.User;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing passkey authentication: {Message}", ex.Message);
            throw;
        }
    }

    public async Task<List<UserPasskeyDto>> GetUserPasskeysAsync(int userId)
    {
        try
        {
            var passkeys = await _context.UserPasskeys
                .Where(p => p.UserId == userId && p.IsActive)
                .Select(p => new UserPasskeyDto
                {
                    Id = p.Id,
                    Name = p.Name,
                    DeviceType = p.DeviceType,
                    CreatedAt = p.CreatedAt,
                    LastUsedAt = p.LastUsedAt,
                    SignCount = p.SignCount,
                    IsActive = p.IsActive
                })
                .ToListAsync();

            return passkeys;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving passkeys for user {UserId}", userId);
            throw;
        }
    }

    public async Task RemovePasskeyAsync(int userId, int passkeyId)
    {
        try
        {
            var passkey = await _context.UserPasskeys
                .FirstOrDefaultAsync(p => p.Id == passkeyId && p.UserId == userId);

            if (passkey == null)
                throw new ArgumentException("Passkey not found");

            passkey.IsActive = false;

            // Check if this was the last active passkey
            var remainingPasskeys = await _context.UserPasskeys
                .Where(p => p.UserId == userId && p.IsActive && p.Id != passkeyId)
                .AnyAsync();

            // Update user's HasPasskeyRegistered flag if no active passkeys remain
            if (!remainingPasskeys)
            {
                var user = await _context.Users.FindAsync(userId);
                if (user != null)
                {
                    user.HasPasskeyRegistered = false;
                    _logger.LogInformation("Updated HasPasskeyRegistered to false for user {UserId} - no active passkeys remain", userId);
                }
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("Passkey {PasskeyId} removed for user {UserId}. Remaining active passkeys: {HasRemaining}", 
                passkeyId, userId, remainingPasskeys);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing passkey {PasskeyId} for user {UserId}", passkeyId, userId);
            throw;
        }
    }

    public async Task UpdatePasskeyAsync(int userId, int passkeyId, PasskeyUpdateDto request)
    {
        try
        {
            var passkey = await _context.UserPasskeys
                .FirstOrDefaultAsync(p => p.Id == passkeyId && p.UserId == userId);

            if (passkey == null)
                throw new ArgumentException("Passkey not found");

            passkey.Name = request.Name;

            await _context.SaveChangesAsync();

            _logger.LogInformation("Passkey {PasskeyId} updated for user {UserId}", passkeyId, userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating passkey {PasskeyId} for user {UserId}", passkeyId, userId);
            throw;
        }
    }

    public async Task<UserMfaDetailsDto> GetUserMfaDetailsAsync(int userId)
    {
        try
        {
            var user = await _userRepository.GetByIdAsync(userId);
            if (user == null)
                throw new ArgumentException("User not found", nameof(userId));

            return new UserMfaDetailsDto
            {
                MfaType = user.MfaType.ToString().ToLower(),
                HasMfaEnabled = user.HasMfaEnabled,
                HasPasskeyRegistered = user.HasPasskeyRegistered,
                MfaEnabledAt = user.MfaEnabledAt,
                // Computed from the same source the Plaid gate uses, so the client's
                // "N days left" always agrees with what the server will enforce. The
                // stored column was never written by EnableEmailMfaAsync; the computed
                // value is preferred and the column is a fallback for legacy rows.
                MfaGracePeriodEndsAt = user.MfaType == MfaType.Email && user.MfaEnabledAt.HasValue
                    ? user.MfaEnabledAt.Value.Add(EmailMfaPlaidWindow)
                    : user.MfaGracePeriodEndsAt
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting MFA details for user {UserId}", userId);
            throw;
        }
    }

    #endregion

    #region Private Helper Methods

    private string DetermineDeviceType(string? deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
            return "Unknown";
            
        var lowerName = deviceName.ToLowerInvariant();
        
        if (lowerName.Contains("iphone") || lowerName.Contains("android") || lowerName.Contains("mobile"))
            return "Mobile";
        if (lowerName.Contains("ipad") || lowerName.Contains("tablet"))
            return "Tablet";
        if (lowerName.Contains("mac") || lowerName.Contains("windows") || lowerName.Contains("linux"))
            return "Desktop";
        if (lowerName.Contains("key") || lowerName.Contains("yubikey"))
            return "Security Key";
            
        return "Unknown";
    }

    private string GenerateRandomCode()
    {
        using var rng = RandomNumberGenerator.Create();
        var bytes = new byte[4];
        rng.GetBytes(bytes);
        var number = Math.Abs(BitConverter.ToInt32(bytes, 0));
        return (number % 1000000).ToString("D6");
    }

    private string GenerateTotpSecret()
    {
        // Use Otp.NET's secure key generation (160 bits / 20 bytes)
        var key = KeyGeneration.GenerateRandomKey(20);
        return Base32Encoding.ToString(key);
    }

    private string[] GenerateRecoveryCodes()
    {
        var codes = new string[8];
        using var rng = RandomNumberGenerator.Create();
        
        for (int i = 0; i < codes.Length; i++)
        {
            var bytes = new byte[5];
            rng.GetBytes(bytes);
            codes[i] = Convert.ToHexString(bytes)[..8].ToLowerInvariant();
        }
        
        return codes;
    }

    private string GenerateQrCodeDataUrl(string qrCodeText)
    {
        try
        {
            _logger.LogDebug("Generating minimal QR code for text: {QrCodeText}", qrCodeText);
            
            if (string.IsNullOrEmpty(qrCodeText))
            {
                _logger.LogError("QR code text is null or empty");
                throw new ArgumentException("QR code text cannot be null or empty", nameof(qrCodeText));
            }

            using var qrGenerator = new QRCodeGenerator();
            // Use lowest error correction level for absolute minimum size
            using var qrCodeData = qrGenerator.CreateQrCode(qrCodeText, ECCLevel.L);
            
            // Use high resolution PNG for crisp display
            try 
            {
                using var qrCode = new PngByteQRCode(qrCodeData);
                // Use 8 pixels per module for high resolution while keeping same data
                var pngBytes = qrCode.GetGraphic(8, new byte[] { 0, 0, 0 }, new byte[] { 255, 255, 255 });
                
                var dataUrl = $"data:image/png;base64,{Convert.ToBase64String(pngBytes)}";
                
                _logger.LogInformation("Generated high-res PNG QR code: {ByteCount} bytes, modules: {ModuleCount}x{ModuleCount} (8px/module), PNG size: {PngSize} bytes", 
                    pngBytes.Length, qrCodeData.ModuleMatrix.Count, qrCodeData.ModuleMatrix.Count, pngBytes.Length);
                
                return dataUrl;
            }
            catch (Exception pngEx)
            {
                _logger.LogError(pngEx, "PNG QR code generation failed");
                throw new InvalidOperationException("Failed to generate QR code", pngEx);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating QR code for text: {QrCodeText}", qrCodeText);
            throw new InvalidOperationException("Failed to generate QR code", ex);
        }
    }

    private string FormatSecretForManualEntry(string secret)
    {
        // Format secret for manual entry (groups of 4 characters)
        return string.Join(" ", secret.Chunk(4).Select(x => new string(x)));
    }

    #endregion

    #region WebAuthn Helper Methods

    /// <summary>
    /// Get existing credentials for a user to prevent duplicate registrations
    /// </summary>
    private async Task<object[]> GetExistingCredentialsAsync(int userId)
    {
        var existingPasskeys = await _context.UserPasskeys
            .Where(p => p.UserId == userId && p.IsActive)
            .Select(p => new 
            { 
                id = p.CredentialId,
                type = "public-key"
            })
            .ToArrayAsync();

        return existingPasskeys.Cast<object>().ToArray();
    }

    /// <summary>
    /// Pad base64 string for proper decoding
    /// </summary>
    private string PadBase64String(string base64)
    {
        // Convert URL-safe base64 to standard base64
        var standardBase64 = base64.Replace('-', '+').Replace('_', '/');

        // Pad with = characters as needed
        var padding = (4 - (standardBase64.Length % 4)) % 4;
        return standardBase64 + new string('=', padding);
    }

    /// <summary>
    /// WebAuthn credential IDs arrive base64url- or standard-base64-encoded, with or
    /// without padding, depending on the platform/authenticator (the Flutter "passkeys"
    /// plugin encodes the id differently for registration vs. authentication). Normalize
    /// to canonical unpadded base64url so new rows are stored consistently.
    /// </summary>
    internal static string NormalizeCredentialId(string credentialId)
    {
        var bytes = TryDecodeBase64Any(credentialId);
        if (bytes == null)
            return credentialId; // Not decodable — store as received rather than throw.

        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Returns every base64/base64url (padded and unpadded) encoding of the same
    /// credential bytes, so a lookup matches regardless of how the row was stored.
    /// This keeps pre-existing (un-normalized) rows matchable without a data migration.
    /// </summary>
    internal static List<string> CredentialIdVariants(string credentialId)
    {
        var variants = new List<string> { credentialId };

        var bytes = TryDecodeBase64Any(credentialId);
        if (bytes != null)
        {
            var standardPadded = Convert.ToBase64String(bytes);
            var standardUnpadded = standardPadded.TrimEnd('=');
            var urlPadded = standardPadded.Replace('+', '-').Replace('/', '_');
            var urlUnpadded = urlPadded.TrimEnd('=');

            foreach (var v in new[] { standardPadded, standardUnpadded, urlPadded, urlUnpadded })
            {
                if (!variants.Contains(v))
                    variants.Add(v);
            }
        }

        return variants;
    }

    private static byte[]? TryDecodeBase64Any(string value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        var standard = value.Replace('-', '+').Replace('_', '/');
        var padding = (4 - (standard.Length % 4)) % 4;
        standard += new string('=', padding);

        try
        {
            return Convert.FromBase64String(standard);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool IsSupportedStoredKey(string storedPublicKey)
    {
        try
        {
            return WebAuthnVerifier.IsSupportedKey(Convert.FromBase64String(storedPublicKey));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Verify the WebAuthn assertion signature against the passkey's stored COSE public key.
    /// </summary>
    /// <summary>
    /// clientDataJSON's origin must be this site (WebAuthnSettings.Origin) or one listed in AllowedOrigins.
    /// The rpIdHash already ties the passkey to the domain; this also stops a page on another subdomain
    /// from relaying a sign-in.
    /// </summary>
    private void RequireAllowedOrigin(JsonElement clientData)
    {
        var origin = clientData.TryGetProperty("origin", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
        if (!_webAuthnSettings.IsAllowedOrigin(origin))
        {
            _logger.LogWarning("Passkey request from origin {Origin} refused (expected {Expected})", origin, _webAuthnSettings.Origin);
            throw new InvalidOperationException("This passkey request didn't come from Buoy Money");
        }
    }

    private bool VerifyAssertionSignature(string authenticatorDataB64, string clientDataJSONB64,
        string signatureB64, string storedPublicKey)
    {
        try
        {
            return WebAuthnVerifier.VerifyAssertion(
                Convert.FromBase64String(PadBase64String(authenticatorDataB64)),
                Convert.FromBase64String(PadBase64String(clientDataJSONB64)),
                Convert.FromBase64String(PadBase64String(signatureB64)),
                Convert.FromBase64String(storedPublicKey),
                _webAuthnSettings.RelyingPartyId,
                _webAuthnSettings.RequiresUserVerification);
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Passkey assertion had malformed base64");
            return false;
        }
    }

    /// <summary>
    /// Extract sign count from authenticator data
    /// </summary>
    private uint ExtractSignCount(byte[] authenticatorData)
    {
        try
        {
            // Sign count is bytes 33-36 in authenticator data (big-endian uint32)
            if (authenticatorData.Length < 37)
                return 0;

            var signCountBytes = authenticatorData.Skip(33).Take(4).Reverse().ToArray();
            return BitConverter.ToUInt32(signCountBytes, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting sign count from authenticator data");
            return 0;
        }
    }

    #endregion
}
