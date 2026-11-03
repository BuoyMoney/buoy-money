using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

namespace BuoyAI.Infrastructure.Configuration;

/// <summary>
/// Defaults that only make sense on a self-hosted server. They go in as the LOWEST priority
/// configuration source, so an environment variable or appsettings file still wins. Hosted adds
/// nothing, so it behaves exactly as before.
/// </summary>
public static class EditionDefaults
{
  /// <summary>
  /// Self-host defaults:
  /// - a verified pg_dump before every migration (the owner has no ops team to restore from);
  /// - new secrets written in the authenticated v2 encryption format (no older version shares the database);
  /// - the HTTPS redirect off (a LAN install has no certificate), forwarded headers trusted only from loopback unless a proxy is named.
  /// </summary>
  public static readonly IReadOnlyDictionary<string, string?> SelfHost = new Dictionary<string, string?>
  {
    ["Migrations:BackupBeforeMigrate"] = "true",
    ["Encryption:WriteV2"] = "true",
    ["Https:RedirectEnabled"] = "false",
    // Loopback only. Private ranges are not safe to trust by default: on Docker Desktop, rootless
    // Docker and the userland proxy every inbound connection arrives from a private gateway
    // address, so trusting the range would let any LAN or port-forwarded client spoof
    // X-Forwarded-For and X-Forwarded-Proto. The compose file adds the Caddy container's fixed
    // address (ForwardedHeaders__TrustedNetworks__2); other proxies must be named explicitly.
    ["ForwardedHeaders:TrustedNetworks:0"] = "127.0.0.0/8",
    ["ForwardedHeaders:TrustedNetworks:1"] = "::1/128",
  };

  /// <summary>
  /// Adds the edition's defaults under every other source. Returns the edition that was read.
  /// In self-host it also loads generated secrets from <c>BuoyMoney:SecretsDirectory</c>
  /// (default /secrets) when that folder exists: one file per setting, the file name is the
  /// setting with "__" for ":" (so <c>Jwt__Key</c> sets <c>Jwt:Key</c>). They rank above the
  /// defaults and below appsettings and environment variables, so an explicit setting wins. A setting
  /// that is present but blank (an unset compose variable) does not count as explicit: the file wins.
  /// </summary>
  public static EditionInfo Apply(IConfigurationBuilder builder, IConfiguration current)
  {
    var edition = EditionInfo.From(current);
    if (edition.IsSelfHost)
    {
      builder.Sources.Insert(0, new MemoryConfigurationSource { InitialData = SelfHost });
      var dir = current["BuoyMoney:SecretsDirectory"];
      if (string.IsNullOrWhiteSpace(dir)) dir = "/secrets";
      var secrets = ReadSecretFiles(dir);
      if (secrets.Count > 0)
      {
        builder.Sources.Insert(1, new MemoryConfigurationSource { InitialData = secrets });
        // compose.yaml maps every optional setting, so an unset one arrives as an EMPTY environment variable,
        // which would outrank the file. A file wins over a setting that is present but blank.
        var overBlank = secrets
          .Where(kv => current[kv.Key] is { Length: 0 })
          .ToDictionary(kv => kv.Key, kv => kv.Value);
        if (overBlank.Count > 0)
        {
          builder.Sources.Add(new MemoryConfigurationSource { InitialData = overBlank });
        }
      }
    }
    return edition;
  }

  /// <summary>One setting per file. Trailing whitespace is trimmed; empty files and dot files are skipped.</summary>
  public static Dictionary<string, string?> ReadSecretFiles(string directory)
  {
    var result = new Dictionary<string, string?>();
    if (!Directory.Exists(directory)) return result;
    foreach (var file in Directory.EnumerateFiles(directory))
    {
      var name = Path.GetFileName(file);
      if (name.StartsWith('.')) continue;
      var value = File.ReadAllText(file).Trim();
      if (value.Length == 0) continue;
      result[name.Replace("__", ":")] = value;
    }
    return result;
  }
}
