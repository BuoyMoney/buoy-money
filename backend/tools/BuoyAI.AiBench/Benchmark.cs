using System.Diagnostics;
using System.Text.RegularExpressions;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.AiBench;

/// <summary>What happened to one row on one run.</summary>
internal sealed record RowOutcome(
    int RowId,
    string Bucket,
    string Expected,
    string? Predicted,
    double Confidence,
    bool FormatOk,
    bool NotOffered,
    bool Error,
    long Milliseconds,
    int? InputTokens,
    int? OutputTokens,
    decimal? CostUsd,
    string Reply);

internal sealed record BucketScore(string Bucket, int Rows, double Accuracy);

/// <summary>One model's scores over the whole set.</summary>
internal sealed record ModelScore(
    string Model,
    int Rows,
    int Runs,
    int Errors,
    double FormatValidity,
    double Accuracy,
    double GroupAccuracy,
    IReadOnlyList<BucketScore> Buckets,
    double HallucinatedShare,
    double MeanConfidenceWhenRight,
    double MeanConfidenceWhenWrong,
    double ShareToReview,
    double LatencyP50Ms,
    double LatencyP95Ms,
    double MeanInputTokens,
    double MeanOutputTokens,
    double RowsPerMinute,
    decimal CostPer1000Rows,
    double Determinism,
    string Verdict);

/// <summary>
/// Runs the real categorization prompt through a provider for each labelled row, reads the replies
/// with the real parser, and scores them. Nothing here is a stand-in for the product code: the
/// prompt comes from <see cref="AICategorizationService.BuildCategorizationPrompt"/> and the
/// reply is read by <see cref="AICategorizationService.ParseCategorizationResponse"/>, so a good
/// score means users would get good categories.
/// </summary>
internal static class Benchmark
{
    public const double ReviewThreshold = 0.7; // Gemini:CategorizationReviewThreshold default

    private static readonly Regex Shape = new(@"^\s*C?\d+\s*:\s*[0-9.]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static async Task<List<RowOutcome>> RunOnceAsync(
        IAiProvider provider, string model, IReadOnlyList<BenchRow> rows, int concurrency,
        Action<int, int>? progress, CancellationToken ct)
    {
        var customs = SyntheticDataset.CustomCategories();
        var outcomes = new RowOutcome[rows.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var done = 0;

        await Task.WhenAll(rows.Select(async (row, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                outcomes[index] = await RunRowAsync(provider, model, row, customs, ct);
            }
            finally
            {
                gate.Release();
                progress?.Invoke(Interlocked.Increment(ref done), rows.Count);
            }
        }));
        return outcomes.ToList();
    }

    private static async Task<RowOutcome> RunRowAsync(
        IAiProvider provider, string model, BenchRow row, List<Category> customs, CancellationToken ct)
    {
        var prompt = AICategorizationService.BuildCategorizationPrompt(row.Transaction, row.UserPatterns, customs);
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await provider.GenerateTextAsync(new AiTextRequest(prompt, model, "categorization"), ct);
            watch.Stop();
            var reply = result.Text.Trim();
            var formatOk = Shape.IsMatch(reply.Split('\n')[0]);
            var parsed = AICategorizationService.ParseCategorizationResponse(reply, customs);
            var valid = formatOk && (parsed.CustomCategoryId.HasValue || parsed.SystemCategory != TransactionCategory.Uncategorized);
            string? predicted = !valid ? null
                : parsed.CustomCategoryId.HasValue ? "C" + parsed.CustomCategoryId.Value
                : ((int)parsed.SystemCategory).ToString();
            var notOffered = valid && !IsOffered(row.Transaction, parsed);
            return new RowOutcome(row.Id, row.Bucket, row.Expected, predicted, valid ? parsed.Confidence : 0,
                formatOk, notOffered, false, watch.ElapsedMilliseconds, result.InputTokens, result.OutputTokens, result.CostUsd, reply);
        }
        catch (AiUnavailableException ex)
        {
            watch.Stop();
            return new RowOutcome(row.Id, row.Bucket, row.Expected, null, 0, false, false, true,
                watch.ElapsedMilliseconds, null, null, null, ex.Message);
        }
    }

    /// <summary>The prompt offers income categories to credits only, and custom categories to expenses only.</summary>
    private static bool IsOffered(Transaction tx, AICategorizationService.AICategorizationInternalResult parsed)
    {
        bool isExpense = tx.Type == TransactionType.Debit;
        if (parsed.CustomCategoryId != null) return isExpense;
        var category = parsed.SystemCategory;
        bool incomeOnly = category is TransactionCategory.Salary or TransactionCategory.Bonus or TransactionCategory.Investment;
        return isExpense ? !incomeOnly : incomeOnly || category == TransactionCategory.OtherIncome;
    }

    /// <summary>Spending groups for the "near miss" score: right area, wrong category.</summary>
    internal static string Group(string? categoryId)
    {
        if (categoryId == null) return "none";
        if (categoryId.StartsWith('C')) return categoryId;
        return int.Parse(categoryId) switch
        {
            >= 1 and <= 4 => "income",
            >= 10 and <= 14 => "bills",
            20 or 21 => "food",
            22 or 26 => "getting around",
            23 or 24 or 27 or 29 => "lifestyle",
            25 or 28 => "health and learning",
            _ => "other",
        };
    }

    public static ModelScore Score(
        string model, IReadOnlyList<IReadOnlyList<RowOutcome>> runs, double wallSeconds, double? baselineAccuracy)
    {
        var all = runs.SelectMany(r => r).ToList();
        var answered = all.Where(o => !o.Error).ToList();
        int total = all.Count;

        double Rate(int n, int of) => of == 0 ? 0 : (double)n / of;
        var right = answered.Where(o => o.Predicted == o.Expected).ToList();
        var wrongParsed = answered.Where(o => o.Predicted != null && o.Predicted != o.Expected).ToList();

        var buckets = all.GroupBy(o => o.Bucket).OrderBy(g => g.Key)
            .Select(g => new BucketScore(g.Key, g.Count() / runs.Count, Rate(g.Count(o => !o.Error && o.Predicted == o.Expected), g.Count())))
            .ToList();

        var latencies = answered.Select(o => (double)o.Milliseconds).OrderBy(x => x).ToList();
        var withTokens = answered.Where(o => o.InputTokens.HasValue).ToList();
        var costs = answered.Where(o => o.CostUsd.HasValue).ToList();

        // Same row, same answer on every run?
        double determinism = 1;
        if (runs.Count > 1)
        {
            var byRow = all.GroupBy(o => o.RowId).ToList();
            determinism = Rate(byRow.Count(g => g.Select(o => o.Predicted).Distinct().Count() == 1), byRow.Count);
        }

        var accuracy = Rate(right.Count, total);
        var validity = Rate(all.Count(o => o.FormatOk && o.Predicted != null && !o.NotOffered), total);
        var perMinute = wallSeconds > 0 ? runs[0].Count * runs.Count / (wallSeconds / 60.0) : 0;

        return new ModelScore(
            model, runs[0].Count, runs.Count, all.Count(o => o.Error),
            validity, accuracy,
            Rate(answered.Count(o => Group(o.Predicted) == Group(o.Expected) && o.Predicted != null), total),
            buckets,
            Rate(all.Count(o => (o.FormatOk && o.Predicted == null) || o.NotOffered), total),
            right.Count == 0 ? 0 : right.Average(o => o.Confidence),
            wrongParsed.Count == 0 ? 0 : wrongParsed.Average(o => o.Confidence),
            Rate(answered.Count(o => o.Predicted == null || o.Confidence < ReviewThreshold), total),
            Percentile(latencies, 0.5), Percentile(latencies, 0.95),
            withTokens.Count == 0 ? 0 : withTokens.Average(o => o.InputTokens!.Value),
            withTokens.Count == 0 ? 0 : withTokens.Average(o => o.OutputTokens ?? 0),
            perMinute,
            costs.Count == 0 ? 0 : costs.Sum(o => o.CostUsd!.Value) / costs.Count * 1000m,
            determinism,
            Verdict(validity, accuracy, baselineAccuracy));
    }

    /// <summary>
    /// The pass bar proposed in the research: recommended at 98% first-try format validity and
    /// accuracy within 5 points of the baseline; usable at 90% and within 12 points.
    /// </summary>
    internal static string Verdict(double validity, double accuracy, double? baselineAccuracy)
    {
        if (baselineAccuracy is null)
            return validity >= 0.98 ? "format ok, no baseline to compare" : validity >= 0.90 ? "format mostly ok, no baseline to compare" : "not recommended (format)";
        var gap = baselineAccuracy.Value - accuracy;
        if (validity >= 0.98 && gap <= 0.05) return "recommended";
        if (validity >= 0.90 && gap <= 0.12) return "usable, sends more rows to review";
        return "not recommended";
    }

    internal static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var rank = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }
}
