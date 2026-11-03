using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Going.Plaid;
using Going.Plaid.WebhookVerificationKey;
using Microsoft.Extensions.Options;

namespace BuoyAI.API.Attributes;

/// <summary>
/// Validates Plaid webhook signatures using JWT/JWK verification per Plaid documentation
/// https://plaid.com/docs/api/webhooks/webhook-verification/
/// </summary>
public class ValidatePlaidWebhookSignatureAttribute : ActionFilterAttribute
{
    private const string CACHE_KEY_PREFIX = "plaid_jwk_";
    private const int CACHE_EXPIRATION_MINUTES = 60;
    private const int MAX_WEBHOOK_AGE_MINUTES = 5;

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<ValidatePlaidWebhookSignatureAttribute>>();
        var cache = context.HttpContext.RequestServices.GetRequiredService<IMemoryCache>();

        // Get the JWT from the Plaid-Verification header
        var signedJwt = context.HttpContext.Request.Headers["Plaid-Verification"].FirstOrDefault();
        if (string.IsNullOrEmpty(signedJwt))
        {
            logger.LogWarning("Missing Plaid-Verification header in webhook request");
            context.Result = new UnauthorizedObjectResult(new { error = "Missing verification header" });
            return;
        }

        // Get the raw request body
        context.HttpContext.Request.EnableBuffering();
        var bodyStream = context.HttpContext.Request.Body;
        bodyStream.Position = 0;
        
        using var reader = new StreamReader(bodyStream, Encoding.UTF8, leaveOpen: true);
        var requestBody = await reader.ReadToEndAsync();
        bodyStream.Position = 0;

        try
        {
            // Validate the webhook
            var isValid = await ValidateWebhookAsync(signedJwt, requestBody, configuration, cache, logger);
            if (!isValid)
            {
                logger.LogWarning("Invalid Plaid webhook signature");
                context.Result = new UnauthorizedObjectResult(new { error = "Invalid signature" });
                return;
            }

            logger.LogDebug("Plaid webhook signature validated successfully");
            await next();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error validating Plaid webhook signature");
            context.Result = new UnauthorizedObjectResult(new { error = "Validation failed" });
        }
    }

    private static async Task<bool> ValidateWebhookAsync(
        string signedJwt, 
        string requestBody, 
        IConfiguration configuration,
        IMemoryCache cache,
        ILogger logger)
    {
        try
        {
            // Step 1: Decode JWT header without validation to get the key ID
            var handler = new JwtSecurityTokenHandler();
            var jwtToken = handler.ReadJwtToken(signedJwt);
            
            // Step 2: Verify algorithm is ES256
            if (jwtToken.Header.Alg != "ES256")
            {
                logger.LogWarning("Invalid JWT algorithm: {Algorithm}", jwtToken.Header.Alg);
                return false;
            }

            // Step 3: Extract key ID (kid)
            if (!jwtToken.Header.TryGetValue("kid", out var kidObj) || kidObj is not string keyId)
            {
                logger.LogWarning("Missing or invalid kid in JWT header");
                return false;
            }

            // Step 4: Get or fetch the JWK public key
            var cacheKey = $"{CACHE_KEY_PREFIX}{keyId}";
            if (!cache.TryGetValue(cacheKey, out PlaidJwk? jwk) || jwk == null)
            {
                jwk = await FetchPlaidJwkAsync(keyId, configuration, logger);
                if (jwk == null)
                {
                    return false;
                }

                // Cache the key for 1 hour
                cache.Set(cacheKey, jwk, TimeSpan.FromMinutes(CACHE_EXPIRATION_MINUTES));
            }

            // Step 5: Verify JWT signature using the JWK
            var ecdsa = CreateECDsaFromJwk(jwk);
            if (ecdsa == null)
            {
                logger.LogError("Failed to create ECDSA from JWK");
                return false;
            }

            using (ecdsa)
            {
                var parts = signedJwt.Split('.');
                if (parts.Length != 3)
                {
                    return false;
                }

                var headerAndPayload = $"{parts[0]}.{parts[1]}";
                var signature = Base64UrlDecode(parts[2]);
                var data = Encoding.UTF8.GetBytes(headerAndPayload);

                if (!ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256))
                {
                    logger.LogWarning("JWT signature verification failed");
                    return false;
                }
            }

            // Step 6: Verify timestamp (IAT - issued at time).
            // Fail closed: a webhook with no parseable iat is not verifiable and is rejected.
            var iatClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == "iat");
            if (iatClaim == null || !long.TryParse(iatClaim.Value, out var iatValue))
            {
                logger.LogWarning("Webhook JWT missing or invalid 'iat' claim");
                return false;
            }

            var issuedAt = DateTimeOffset.FromUnixTimeSeconds(iatValue);
            var age = DateTimeOffset.UtcNow - issuedAt;
            if (age.TotalMinutes > MAX_WEBHOOK_AGE_MINUTES)
            {
                logger.LogWarning("Webhook too old: {Age} minutes", age.TotalMinutes);
                return false;
            }

            // Step 7: Verify request body hash.
            // Fail closed: a signed JWT with no request_body_sha256 claim cannot bind the
            // body to the signature, so an attacker could replay a valid signature over a
            // tampered body. Reject when the claim is absent or not a string.
            if (!jwtToken.Payload.TryGetValue("request_body_sha256", out var claimedHashObj) ||
                claimedHashObj is not string claimedHash)
            {
                logger.LogWarning("Webhook JWT missing 'request_body_sha256' claim");
                return false;
            }

            var bodyHash = ComputeSha256Hash(requestBody);
            if (!string.Equals(bodyHash, claimedHash, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Request body hash mismatch");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception during webhook validation");
            return false;
        }
    }

    private static async Task<PlaidJwk?> FetchPlaidJwkAsync(string keyId, IConfiguration configuration, ILogger logger)
    {
        try
        {
            var plaidSettings = configuration.GetSection("Plaid");
            var environment = plaidSettings["Environment"]?.ToLowerInvariant() switch
            {
                "sandbox" => Going.Plaid.Environment.Sandbox,
                "development" => Going.Plaid.Environment.Development,
                "production" => Going.Plaid.Environment.Production,
                _ => Going.Plaid.Environment.Sandbox
            };

            var plaidOptions = Options.Create(new Going.Plaid.PlaidOptions
            {
                ClientId = plaidSettings["ClientId"] ?? throw new InvalidOperationException("Plaid ClientId not configured"),
                Secret = plaidSettings["SecretKey"] ?? throw new InvalidOperationException("Plaid SecretKey not configured"),
                Environment = environment
            });

            var plaidClient = new PlaidClient(plaidOptions);
            var request = new WebhookVerificationKeyGetRequest { KeyId = keyId };
            var response = await plaidClient.WebhookVerificationKeyGetAsync(request);

            if (response?.Key == null)
            {
                logger.LogError("Failed to fetch JWK from Plaid for key ID: {KeyId}", keyId);
                return null;
            }

            return new PlaidJwk
            {
                Kty = response.Key.Kty,
                Use = response.Key.Use,
                Alg = response.Key.Alg,
                Kid = response.Key.Kid,
                Crv = response.Key.Crv,
                X = response.Key.X,
                Y = response.Key.Y
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching Plaid JWK for key ID: {KeyId}", keyId);
            return null;
        }
    }

    private static ECDsa? CreateECDsaFromJwk(PlaidJwk jwk)
    {
        try
        {
            var ecdsa = ECDsa.Create();
            var x = Base64UrlDecode(jwk.X);
            var y = Base64UrlDecode(jwk.Y);

            var parameters = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = x,
                    Y = y
                }
            };

            ecdsa.ImportParameters(parameters);
            return ecdsa;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] Base64UrlDecode(string base64Url)
    {
        var base64 = base64Url.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        return Convert.FromBase64String(base64);
    }

    private static string ComputeSha256Hash(string input)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = sha256.ComputeHash(bytes);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}

/// <summary>
/// Simplified JWK model for Plaid webhook verification
/// </summary>
internal class PlaidJwk
{
    public string Kty { get; set; } = string.Empty;
    public string Use { get; set; } = string.Empty;
    public string Alg { get; set; } = string.Empty;
    public string Kid { get; set; } = string.Empty;
    public string Crv { get; set; } = string.Empty;
    public string X { get; set; } = string.Empty;
    public string Y { get; set; } = string.Empty;
}
