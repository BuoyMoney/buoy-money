using BuoyAI.Core.DTOs.Export;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Builds and streams the full data export (GDPR / CCPA portability). Free for every plan.
/// Output is written table by table straight to the destination stream; nothing holds the
/// whole export in memory.
/// </summary>
public interface IUserDataExportService
{
    /// <summary>
    /// Works out what the caller may export. Returns null when the user does not exist.
    /// </summary>
    Task<ExportScope?> ResolveScopeAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>Streams the export as one JSON document.</summary>
    Task WriteJsonAsync(ExportScope scope, Stream destination, CancellationToken cancellationToken = default);

    /// <summary>Streams the export as a zip with one CSV per table plus EXCLUDED.md.</summary>
    Task WriteCsvZipAsync(ExportScope scope, Stream destination, CancellationToken cancellationToken = default);

    /// <summary>Records that the user exported their data (user id and format only).</summary>
    Task RecordExportAsync(int userId, string format, CancellationToken cancellationToken = default);
}
