using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace BuoyAI.Infrastructure.Configuration;

/// <summary>
/// Refuses to start the API with a missing, placeholder or too-short secret. The example
/// settings files ship values like "YOUR_JWT_SECRET_KEY..." so a copy-paste deploy that forgets
/// to set a real key would otherwise run with a publicly known one.
/// </summary>
public static class StartupSecretsValidator
{
  public const string JwtKeySetting = "Jwt:Key";
  public const string EncryptionKeySetting = "Encryption:Key";
  public const string McpSigningKeySetting = "Mcp:SigningKey";
  public const string DebugApiKeySetting = "Debug:ApiKey";

  /// <summary>HMAC-SHA256 wants at least 256 bits; one character is one byte for the ASCII key.</summary>
  public const int MinSigningKeyLength = 32;

  /// <summary>
  /// Below this the data-encryption key is rejected. 16..31 is accepted with a warning because
  /// the legacy format padded short keys, so a deployed key may be shorter than 32.
  /// </summary>
  public const int MinEncryptionKeyLength = 16;
  public const int MinDebugKeyLength = 16;

  // "YOUR_..." / "your-secure-..." (appsettings examples), "change me", "replace me", "<angle>" and friends.
  private static readonly Regex PlaceholderPattern = new(
    @"(^|[^a-z])your[_-]|change[_\- ]?me|replace[_\- ]?me|placeholder|<[^>]*>|\bTODO\b|insert[_\- ]?(key|secret)",
    RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

  // Dev/test keys that appeared in earlier docs and CI. Only refused in Production and Staging.
  private static readonly string[] WeakKeyFragments =
  {
    "letsagoitsameamarioyahookoopatroopagoombabowserdonkeykong",
    "test-jwt-key",
    "development-key",
    "your-jwt-secret-key"
  };

  public sealed record Result(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
  {
    public bool IsValid => Errors.Count == 0;
  }

  public static bool LooksLikePlaceholder(string? value)
  {
    if (string.IsNullOrWhiteSpace(value)) return false;
    return PlaceholderPattern.IsMatch(value) || value.Distinct().Count() < 8;
  }

  public static Result Validate(IConfiguration configuration, string environmentName)
  {
    var errors = new List<string>();
    var warnings = new List<string>();
    var strictEnvironment =
      string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase) ||
      string.Equals(environmentName, "Staging", StringComparison.OrdinalIgnoreCase);

    // Jwt:Key
    var jwt = configuration[JwtKeySetting];
    if (string.IsNullOrWhiteSpace(jwt))
      errors.Add($"{JwtKeySetting} is not set. Set it to a random secret of at least {MinSigningKeyLength} characters (for example `openssl rand -base64 48`).");
    else
    {
      CheckSigningKey(JwtKeySetting, jwt, errors);
      if (strictEnvironment && WeakKeyFragments.Any(w => jwt.Contains(w, StringComparison.OrdinalIgnoreCase)))
        errors.Add($"{JwtKeySetting} is a known development/test key and cannot be used in {environmentName}.");
    }

    // Encryption:Key
    var enc = configuration[EncryptionKeySetting];
    if (string.IsNullOrWhiteSpace(enc))
      errors.Add($"{EncryptionKeySetting} is not set. Set it to a random secret of at least {MinSigningKeyLength} characters (for example `openssl rand -base64 48`).");
    else if (LooksLikePlaceholder(enc))
      errors.Add($"{EncryptionKeySetting} is a placeholder or has almost no variety. Set a real random secret.");
    else if (!IsAscii(enc))
      errors.Add($"{EncryptionKeySetting} must contain only ASCII characters (the AES key is derived from its first 32 characters as bytes).");
    else if (enc.Length < MinEncryptionKeyLength)
      errors.Add($"{EncryptionKeySetting} is too short ({enc.Length} characters). Use at least {MinSigningKeyLength}.");
    else if (enc.Length < MinSigningKeyLength)
      warnings.Add($"{EncryptionKeySetting} is {enc.Length} characters. Use at least {MinSigningKeyLength} when you next rotate it.");
    if (strictEnvironment && !string.IsNullOrWhiteSpace(enc) &&
        WeakKeyFragments.Any(w => enc.Contains(w, StringComparison.OrdinalIgnoreCase)))
      errors.Add($"{EncryptionKeySetting} is a known development/test key and cannot be used in {environmentName}.");

    // Mcp:SigningKey (optional: falls back to Jwt:Key). When set it must be a real key.
    var mcp = configuration[McpSigningKeySetting];
    if (!string.IsNullOrWhiteSpace(mcp))
      CheckSigningKey(McpSigningKeySetting, mcp, errors);
    else if (configuration.GetValue<bool>("Mcp:Enabled"))
      warnings.Add($"{McpSigningKeySetting} is not set; MCP/OAuth access tokens are signed with {JwtKeySetting}. Set a separate key so the two token families cannot be confused.");

    // Debug:ApiKey (optional, staging only). Reject placeholders and short keys.
    var debugKey = configuration[DebugApiKeySetting];
    if (!string.IsNullOrWhiteSpace(debugKey) && LooksLikePlaceholder(debugKey))
      errors.Add($"{DebugApiKeySetting} is a placeholder. Set a real random secret or leave it empty to disable the debug endpoints.");
    else if (!string.IsNullOrWhiteSpace(debugKey) && debugKey.Length < MinDebugKeyLength)
      errors.Add($"{DebugApiKeySetting} is too short ({debugKey.Length} characters). Use at least {MinDebugKeyLength} random characters or leave it empty to disable the debug endpoints.");

    return new Result(errors, warnings);
  }

  /// <summary>Throws on the first startup with bad secrets; reports warnings through <paramref name="warn"/>.</summary>
  public static void ValidateOrThrow(IConfiguration configuration, string environmentName, Action<string>? warn = null)
  {
    var result = Validate(configuration, environmentName);
    foreach (var w in result.Warnings) warn?.Invoke(w);
    if (!result.IsValid)
      throw new InvalidOperationException(
        "Refusing to start: " + string.Join(" ", result.Errors));
  }

  /// <summary>
  /// Key used to sign and validate MCP/OAuth access tokens: <c>Mcp:SigningKey</c> when set,
  /// otherwise the first-party <c>Jwt:Key</c> so existing hosted deployments keep working.
  /// </summary>
  public static string ResolveMcpSigningKey(IConfiguration configuration)
  {
    var mcp = configuration[McpSigningKeySetting];
    if (!string.IsNullOrWhiteSpace(mcp)) return mcp;
    return configuration[JwtKeySetting]
      ?? throw new InvalidOperationException("JWT key not configured");
  }

  private static bool IsAscii(string value) => value.All(c => c <= 0x7F);

  private static void CheckSigningKey(string setting, string value, List<string> errors)
  {
    if (LooksLikePlaceholder(value))
      errors.Add($"{setting} is a placeholder or has almost no variety. Set a real random secret.");
    else if (!IsAscii(value))
      errors.Add($"{setting} must contain only ASCII characters (tokens are signed with its ASCII bytes).");
    else if (value.Length < MinSigningKeyLength)
      errors.Add($"{setting} must be at least {MinSigningKeyLength} characters (it is {value.Length}).");
  }
}
