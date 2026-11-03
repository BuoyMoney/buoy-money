using System.Security.Cryptography;
using System.Text;
using BuoyAI.Core.DTOs.Import;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace BuoyAI.Infrastructure.Services.Import;

/// <summary>What the planner decided about one parsed row.</summary>
public sealed record RowPlan(string Class, int? MatchedTransactionId, string? Reason);

/// <summary>
/// Builds the stored identity of an imported row and classifies parsed rows against what is
/// already in the account: same source id (exact), same amount near the same date with a similar
/// payee (likely), same amount near the same date with a different payee (possible).
/// </summary>
public sealed class ImportPlanner
{
  /// <summary>How far apart bank posting and purchase dates may be, in days.</summary>
  public const int FuzzyWindowDays = 3;
  public const double SimilarityThreshold = 0.6;
  private const int MaxIdLength = 100;

  private readonly BuoyAIDbContext _db;

  public ImportPlanner(BuoyAIDbContext db)
  {
    _db = db;
  }

  /// <summary>
  /// The value stored in the unique PlaidTransactionId column. Scoped by source and target
  /// account, so the same bank id arriving in two accounts can never collide, and capped at the
  /// column width.
  /// </summary>
  public static string BuildExternalId(ImportSource source, int accountId, ParsedImportRow row)
  {
    var src = source.ToString().ToLowerInvariant();
    var tail = !string.IsNullOrWhiteSpace(row.SourceId)
      ? row.SourceId!.Trim()
      : Sha1Hex($"{row.Date:yyyyMMdd}|{row.Amount:0.00}|{row.Payee.Trim().ToLowerInvariant()}|{row.Occurrence}")[..20];

    var id = $"import:{src}:{accountId}:{tail}";
    if (id.Length <= MaxIdLength) return id;
    return $"import:{src}:{accountId}:{Sha1Hex(tail)}";
  }

  private static string Sha1Hex(string input) =>
    Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

  private sealed record Candidate(
    int Id,
    DateOnly Date,
    DateOnly? AuthorizedDate,
    decimal Amount,
    string? Description,
    string? MerchantName,
    string? OriginalDescription,
    bool FromImport);

  /// <summary>
  /// Classifies <paramref name="rows"/> (all of them belong to one target account). A row that
  /// failed to parse is invalid. Rows are matched one-to-one, so two identical purchases in the
  /// file are not both blamed on a single stored transaction.
  /// </summary>
  public async Task<Dictionary<int, RowPlan>> ClassifyAsync(
    ImportSource source,
    int accountId,
    IReadOnlyList<ParsedImportRow> rows,
    CancellationToken cancellationToken = default)
  {
    var plans = new Dictionary<int, RowPlan>();
    var valid = new List<ParsedImportRow>();
    foreach (var row in rows)
    {
      if (row.Error != null || !row.Date.HasValue)
        plans[row.RowId] = new RowPlan(ImportRowClass.Invalid, null, row.Error ?? "The date could not be read.");
      else
        valid.Add(row);
    }
    if (valid.Count == 0) return plans;

    // 1. Exact: the identity this importer would store is already there.
    var keyByRow = valid.ToDictionary(r => r.RowId, r => BuildExternalId(source, accountId, r));
    var existingIds = new Dictionary<string, int>();
    foreach (var chunk in keyByRow.Values.Distinct().Chunk(1000))
    {
      var found = await _db.Transactions.AsNoTracking()
        .Where(t => chunk.Contains(t.PlaidTransactionId))
        .Select(t => new { t.Id, t.PlaidTransactionId })
        .ToListAsync(cancellationToken);
      foreach (var f in found) existingIds[f.PlaidTransactionId] = f.Id;
    }

    var remaining = new List<ParsedImportRow>();
    var seenKeys = new HashSet<string>();
    foreach (var row in valid)
    {
      var key = keyByRow[row.RowId];
      if (existingIds.TryGetValue(key, out var existingId))
        plans[row.RowId] = new RowPlan(ImportRowClass.DuplicateExact, existingId, "Already imported.");
      else if (!seenKeys.Add(key))
        // Same identity twice in one file: only the first can be stored (the key is unique).
        plans[row.RowId] = new RowPlan(ImportRowClass.DuplicateExact, null, "Repeated earlier in this file.");
      else
        remaining.Add(row);
    }
    if (remaining.Count == 0) return plans;

    // 2. Fuzzy, against a thin projection of only the dates the file touches.
    var min = remaining.Min(r => r.Date!.Value).AddDays(-FuzzyWindowDays);
    var max = remaining.Max(r => r.Date!.Value).AddDays(FuzzyWindowDays);
    var minUtc = DateTime.SpecifyKind(min.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
    var maxUtc = DateTime.SpecifyKind(max.ToDateTime(TimeOnly.MaxValue), DateTimeKind.Utc);

    var raw = await _db.Transactions.AsNoTracking()
      .Where(t => t.AccountId == accountId && t.Date >= minUtc && t.Date <= maxUtc && t.ParentTransactionId == null)
      .Select(t => new
      {
        t.Id,
        t.Date,
        t.AuthorizedDate,
        t.Amount,
        t.Description,
        t.MerchantName,
        t.OriginalDescription,
        t.PlaidTransactionId
      })
      .ToListAsync(cancellationToken);

    var candidates = raw.Select(t => new Candidate(
      t.Id,
      DateOnly.FromDateTime(t.Date),
      t.AuthorizedDate.HasValue ? DateOnly.FromDateTime(t.AuthorizedDate.Value) : null,
      t.Amount,
      t.Description,
      t.MerchantName,
      t.OriginalDescription,
      t.PlaidTransactionId.StartsWith("import", StringComparison.Ordinal))).ToList();

    var used = new HashSet<int>();
    foreach (var row in remaining.OrderBy(r => r.Date))
    {
      var best = FindMatch(row, candidates, used);
      if (best == null)
      {
        plans[row.RowId] = new RowPlan(ImportRowClass.New, null, null);
        continue;
      }
      used.Add(best.Value.Candidate.Id);
      var (cand, similarity, dayGap) = best.Value;
      var what = DescribeCandidate(cand);
      if (similarity >= SimilarityThreshold)
        plans[row.RowId] = new RowPlan(ImportRowClass.DuplicateLikely, cand.Id, $"Looks like {what}.");
      else
        plans[row.RowId] = new RowPlan(ImportRowClass.PossibleDuplicate, cand.Id, $"Same amount {(dayGap == 0 ? "and day" : "within a day or two")} as {what}.");
    }
    return plans;
  }

  private static string DescribeCandidate(Candidate c)
  {
    var name = !string.IsNullOrWhiteSpace(c.MerchantName) ? c.MerchantName : c.Description;
    return $"\"{name}\" on {c.Date:yyyy-MM-dd}";
  }

  private static (Candidate Candidate, double Similarity, int DayGap)? FindMatch(
    ParsedImportRow row,
    List<Candidate> candidates,
    HashSet<int> used)
  {
    (Candidate, double, int)? best = null;
    var rowDate = row.Date!.Value;
    var rowTexts = new[] { row.Payee, row.OriginalDescription }.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();

    foreach (var cand in candidates)
    {
      if (used.Contains(cand.Id) || cand.Amount != row.Amount) continue;

      // Rows that came from an import already use the file's own dates, so they must agree exactly.
      var window = cand.FromImport ? 0 : FuzzyWindowDays;
      var gap = Math.Abs(rowDate.DayNumber - cand.Date.DayNumber);
      if (cand.AuthorizedDate.HasValue)
        gap = Math.Min(gap, Math.Abs(rowDate.DayNumber - cand.AuthorizedDate.Value.DayNumber));
      if (gap > window) continue;

      var candTexts = new[] { cand.MerchantName, cand.Description, cand.OriginalDescription }
        .Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();

      double similarity;
      if (rowTexts.Length == 0 || candTexts.Length == 0)
      {
        // Nothing to compare: an amount-and-day match is only a possibility.
        similarity = 0;
        if (gap > 1) continue;
      }
      else
      {
        similarity = rowTexts.Max(a => candTexts.Max(b => ImportText.PayeeSimilarity(a, b)));
        // Different payees only count when the day lines up closely.
        if (similarity < SimilarityThreshold && gap > 1) continue;
      }

      if (best == null
          || similarity > best.Value.Item2
          || (similarity == best.Value.Item2 && gap < best.Value.Item3))
        best = (cand, similarity, gap);
    }
    return best;
  }
}
