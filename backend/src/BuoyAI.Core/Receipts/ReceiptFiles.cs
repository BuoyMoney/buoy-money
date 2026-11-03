namespace BuoyAI.Core.Receipts;

/// <summary>A screenshot, photo or PDF the model reads.</summary>
public record ReceiptFile(string MimeType, byte[] Data);

/// <summary>
/// What a receipt upload may contain. Amazon (since 2026-07-08) and Walmart order emails carry only
/// a total, so receipts come in as a screenshot of the order page, a photo of a paper receipt, or
/// an order or invoice page saved as a PDF. A long order can take a few screenshots.
/// </summary>
public static class ReceiptFiles
{
  public const long MaxFileBytes = 8 * 1024 * 1024;
  public const long MaxTotalBytes = 16 * 1024 * 1024;
  /// <summary>One receipt; a long order can take several screenshots.</summary>
  public const int MaxFiles = 8;

  public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
  {
    "application/pdf",
    "image/jpeg",
    "image/png",
    "image/webp",
    "image/heic",
    "image/heif",
  };

  /// <summary>The type the content actually is, judged from its first bytes; null when it isn't one of <see cref="Types"/>.</summary>
  public static string? Sniff(ReadOnlySpan<byte> data)
  {
    foreach (var t in new[] { "application/pdf", "image/jpeg", "image/png", "image/webp", "image/heic" })
      if (LooksLike(data, t))
        return t;
    return null;
  }

  // Major brands of HEIC/HEIF stills. MP4, MOV and AVIF share the ftyp box but not these brands.
  private static readonly string[] HeifBrands = { "heic", "heix", "heim", "heis", "hevc", "hevx", "mif1", "msf1" };

  private static bool IsHeifBrand(ReadOnlySpan<byte> data)
  {
    if (data.Length < 12)
      return false;
    var brand = System.Text.Encoding.ASCII.GetString(data.Slice(8, 4));
    return Array.IndexOf(HeifBrands, brand) >= 0;
  }

  /// <summary>Do the first bytes match the declared type? A renamed file of another kind is refused.</summary>
  public static bool LooksLike(ReadOnlySpan<byte> data, string mimeType)
  {
    static bool At(ReadOnlySpan<byte> d, int offset, string ascii) =>
      d.Length >= offset + ascii.Length
      && d.Slice(offset, ascii.Length).SequenceEqual(System.Text.Encoding.ASCII.GetBytes(ascii));

    return mimeType.ToLowerInvariant() switch
    {
      "application/pdf" => At(data, 0, "%PDF"),
      "image/jpeg" => data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF,
      "image/png" => data.Length >= 8 && data[0] == 0x89 && At(data, 1, "PNG"),
      "image/webp" => At(data, 0, "RIFF") && At(data, 8, "WEBP"),
      "image/heic" or "image/heif" => At(data, 4, "ftyp") && IsHeifBrand(data),
      _ => false,
    };
  }
}
