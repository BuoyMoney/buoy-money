using System.Text.RegularExpressions;

namespace BuoyAI.Core.Utilities;

/// <summary>
/// Paths that carry a live secret in the URL itself, and the redacted form to log or report instead.
/// A Cloud link code (<c>link-sessions/BCDF-GH23</c>) lets whoever holds it approve a server onto an
/// account for ten minutes, so it never goes into logs, breadcrumbs, traces or error reports.
/// </summary>
public static partial class SecretPathRedaction
{
  public const string Placeholder = "[code]";

  // The segment after link-sessions/, unless it is the server's own "poll" call (no code in that path).
  [GeneratedRegex(@"(link-sessions/)(?!poll(?:[/?#]|$))[^/?#]+", RegexOptions.IgnoreCase)]
  private static partial Regex LinkSessionCode();

  /// <summary>The path or URL with any link code replaced by <see cref="Placeholder"/>. Null stays null.</summary>
  public static string? Redact(string? pathOrUrl) =>
    string.IsNullOrEmpty(pathOrUrl) ? pathOrUrl : LinkSessionCode().Replace(pathOrUrl, "$1" + Placeholder);

  /// <summary>True when the path or URL carries a link code or a SimpleFIN one-time claim token.</summary>
  public static bool CarriesSecret(string? pathOrUrl) =>
    !string.IsNullOrEmpty(pathOrUrl)
    && (pathOrUrl.Contains("/simplefin/claim", StringComparison.OrdinalIgnoreCase) || LinkSessionCode().IsMatch(pathOrUrl));
}
