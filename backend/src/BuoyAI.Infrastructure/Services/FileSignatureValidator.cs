using System.Text;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Verifies that an uploaded file's actual content (magic bytes / structure) is consistent
/// with its declared MIME type (issue #367). MIME type from the request is attacker-controlled,
/// so this is the real guard against e.g. an executable renamed to <c>.pdf</c>.
///
/// Conservative by design: it rejects content that clearly doesn't match the declared type
/// (binary where text is expected, wrong magic bytes), while allowing the legitimate financial
/// formats we accept (PDF, OFX/QFX, QIF, CSV, TXT).
/// </summary>
public static class FileSignatureValidator
{
  /// <summary>Number of leading bytes to inspect.</summary>
  public const int HeaderSize = 512;

  /// <summary>
  /// Returns true if <paramref name="header"/> (the first bytes of the file) is consistent with
  /// <paramref name="mimeType"/>. Unknown MIME types are rejected (callers validate the allow-list first).
  /// </summary>
  public static bool IsContentConsistent(ReadOnlySpan<byte> header, string mimeType)
  {
    if (header.IsEmpty) return false;

    switch (mimeType)
    {
      case "application/pdf":
        // "%PDF"
        return StartsWith(header, "%PDF");

      case "image/jpeg":
        // JPEG SOI marker: FF D8 FF
        return header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;

      case "image/png":
        // PNG signature: 89 50 4E 47 0D 0A 1A 0A
        return header.Length >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E
            && header[3] == 0x47 && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A;

      case "image/webp":
        // RIFF container with a "WEBP" fourCC at offset 8: "RIFF"...."WEBP"
        return header.Length >= 12 && StartsWith(header, "RIFF")
            && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P';

      case "application/x-ofx":
      case "application/vnd.intu.qfx":
      {
        // OFX/QFX is SGML (starts with "OFXHEADER:") or XML ("<?xml" then "<OFX>").
        var text = AsciiPeek(header);
        return text.Contains("OFXHEADER", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<OFX", StringComparison.OrdinalIgnoreCase)
            || text.TrimStart().StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
      }

      case "application/qif":
      {
        // QIF files begin with a header line like "!Type:Bank", "!Account", "!Option:".
        var text = AsciiPeek(header);
        return text.Contains("!Type:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("!Account", StringComparison.OrdinalIgnoreCase)
            || text.Contains("!Option", StringComparison.OrdinalIgnoreCase);
      }

      case "text/csv":
      case "text/plain":
        // No signature for text — require it to actually be text (no NUL bytes / control noise),
        // which rejects executables, archives, images, etc. masquerading as .csv/.txt.
        return LooksLikeText(header);

      default:
        return false;
    }
  }

  private static bool StartsWith(ReadOnlySpan<byte> header, string ascii)
  {
    if (header.Length < ascii.Length) return false;
    for (var i = 0; i < ascii.Length; i++)
    {
      if (header[i] != (byte)ascii[i]) return false;
    }
    return true;
  }

  private static string AsciiPeek(ReadOnlySpan<byte> header) =>
    Encoding.ASCII.GetString(header);

  /// <summary>
  /// Heuristic: text files have no NUL bytes and are overwhelmingly printable/whitespace.
  /// A UTF-8/16 BOM is allowed. Catches binary payloads renamed to a text extension.
  /// </summary>
  private static bool LooksLikeText(ReadOnlySpan<byte> header)
  {
    var start = 0;
    // Skip a UTF-8 BOM.
    if (header.Length >= 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
      start = 3;

    var printable = 0;
    var total = 0;
    for (var i = start; i < header.Length; i++)
    {
      var b = header[i];
      if (b == 0x00) return false; // NUL => binary
      total++;
      // Tab, LF, CR, or printable ASCII / high-bytes (UTF-8 continuation) count as "texty".
      if (b == 0x09 || b == 0x0A || b == 0x0D || b >= 0x20)
        printable++;
    }
    if (total == 0) return true; // only a BOM — allow
    return (double)printable / total >= 0.95;
  }
}
