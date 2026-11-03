using BuoyAI.AiBench;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>The benchmark tool itself: a stable dataset, the real prompt and parser, honest scoring.</summary>
public class AiBenchmarkTests
{
    [Fact]
    public void Dataset_SameSeed_SameRows_DifferentSeed_DifferentRows()
    {
        var a = SyntheticDataset.Build(42);
        var b = SyntheticDataset.Build(42);
        var c = SyntheticDataset.Build(7);

        a.Select(r => (r.Id, r.Expected, r.Transaction.Description, r.Transaction.Amount))
            .Should().Equal(b.Select(r => (r.Id, r.Expected, r.Transaction.Description, r.Transaction.Amount)));
        a.Select(r => r.Transaction.Amount).Should().NotEqual(c.Select(r => r.Transaction.Amount));
    }

    [Fact]
    public void Dataset_CoversAllFourBuckets_AndEveryExpectedLabelIsOfferedByThePrompt()
    {
        var rows = SyntheticDataset.Build();
        var customs = SyntheticDataset.CustomCategories();

        rows.Select(r => r.Bucket).Distinct().Should().BeEquivalentTo(
            new[] { SyntheticDataset.Plain, SyntheticDataset.Income, SyntheticDataset.Split, SyntheticDataset.Custom });
        rows.Count.Should().BeGreaterThan(300);

        foreach (var row in rows)
        {
            var prompt = BuoyAI.Infrastructure.Services.AICategorizationService.BuildCategorizationPrompt(row.Transaction, row.UserPatterns, customs);
            prompt.Should().Contain("- " + row.Expected + ":", $"row {row.Id} expects {row.Expected} so the prompt must offer it");
        }
    }

    [Fact]
    public void Dataset_IncomeRowsAreCredits_AndExpenseRowsAreDebits()
    {
        var rows = SyntheticDataset.Build();
        rows.Where(r => r.Bucket == SyntheticDataset.Income).Should().OnlyContain(r => r.Transaction.Type == TransactionType.Credit);
        rows.Where(r => r.Bucket != SyntheticDataset.Income).Should().OnlyContain(r => r.Transaction.Type == TransactionType.Debit);
    }

    [Fact]
    public void SplitRows_CarryTheHistoryBlock_ThatTellsTheClustersApart()
    {
        var split = SyntheticDataset.Build().First(r => r.Bucket == SyntheticDataset.Split);
        var prompt = BuoyAI.Infrastructure.Services.AICategorizationService.BuildCategorizationPrompt(
            split.Transaction, split.UserPatterns, SyntheticDataset.CustomCategories());

        prompt.Should().Contain("MORE THAN ONE category");
    }

    [Fact]
    public void Take_KeepsTheBucketMix_AndIsDeterministic()
    {
        var all = SyntheticDataset.Build();
        var some = SyntheticDataset.Take(all, 60);

        some.Count.Should().BeInRange(55, 70);
        some.Select(r => r.Bucket).Distinct().Should().HaveCount(4);
        SyntheticDataset.Take(all, 60).Select(r => r.Id).Should().Equal(some.Select(r => r.Id));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(60)]
    [InlineData(333)]
    public void Take_ReturnsExactlyTheRequestedRowCount(int count)
    {
        SyntheticDataset.Take(SyntheticDataset.Build(), count).Should().HaveCount(count);
    }

    [Fact]
    public async Task EndToEnd_WithTheFakeOracle_ScoresRealPromptsAndParser()
    {
        var rows = SyntheticDataset.Take(SyntheticDataset.Build(), 120);
        var oracle = new OracleProvider(rows, accuracy: 1.0);

        // accuracy 1.0 still sends ~3% garbage, so format validity is just under 100%.
        var outcomes = await Benchmark.RunOnceAsync(oracle, "m", rows, 4, null, CancellationToken.None);
        var score = Benchmark.Score("m", [outcomes], wallSeconds: 1, baselineAccuracy: null);

        score.Rows.Should().Be(rows.Count);
        score.Accuracy.Should().BeGreaterThan(0.9);
        score.FormatValidity.Should().BeGreaterThan(0.9);
        score.Buckets.Select(b => b.Bucket).Should().BeEquivalentTo("plain", "income", "split", "custom");
        score.Errors.Should().Be(0);
    }

    private static RowOutcome Outcome(int id, string expected, string? predicted, double confidence = 0.9,
        bool formatOk = true, bool notOffered = false, bool error = false, long ms = 100) =>
        new(id, "plain", expected, predicted, confidence, formatOk, notOffered, error, ms, 500, 10, 0m, "");

    [Fact]
    public void Score_CountsRightWrongInvalidAndErrors_Separately()
    {
        var outcomes = new List<RowOutcome>
        {
            Outcome(1, "21", "21", 0.95),
            Outcome(2, "21", "20", 0.60),                       // wrong but same group (food)
            Outcome(3, "22", "13", 0.80),                       // wrong, other group
            Outcome(4, "24", null, 0, formatOk: false),         // unreadable
            Outcome(5, "24", null, 0, formatOk: false, error: true), // call failed
        };

        var score = Benchmark.Score("m", [outcomes], 60, baselineAccuracy: null);

        score.Accuracy.Should().BeApproximately(0.2, 1e-9);      // 1 of 5
        score.GroupAccuracy.Should().BeApproximately(0.4, 1e-9); // 21 right, 21->20 same group
        score.FormatValidity.Should().BeApproximately(0.6, 1e-9);
        score.Errors.Should().Be(1);
        score.MeanConfidenceWhenRight.Should().Be(0.95);
        score.MeanConfidenceWhenWrong.Should().BeApproximately(0.70, 1e-9);
        score.ShareToReview.Should().BeApproximately(0.4, 1e-9); // low confidence (1 row) + unreadable (1 row), out of all 5
        score.RowsPerMinute.Should().BeApproximately(5, 1e-9);
    }

    [Fact]
    public void Score_Determinism_IsTheShareOfRowsAnsweredTheSameOnEveryRun()
    {
        var run1 = new List<RowOutcome> { Outcome(1, "21", "21"), Outcome(2, "21", "20") };
        var run2 = new List<RowOutcome> { Outcome(1, "21", "21"), Outcome(2, "21", "24") };

        Benchmark.Score("m", [run1, run2], 60, null).Determinism.Should().BeApproximately(0.5, 1e-9);
    }

    [Theory]
    [InlineData(0.99, 0.88, 0.90, "recommended")]
    [InlineData(0.99, 0.80, 0.90, "usable, sends more rows to review")]
    [InlineData(0.95, 0.85, 0.90, "usable, sends more rows to review")]
    [InlineData(0.99, 0.70, 0.90, "not recommended")]
    [InlineData(0.80, 0.90, 0.90, "not recommended")]
    public void Verdict_FollowsThePassBarFromTheResearch(double validity, double accuracy, double baseline, string expected)
    {
        Benchmark.Verdict(validity, accuracy, baseline).Should().Be(expected);
    }

    [Fact]
    public void Verdict_WithoutABaseline_NeverSaysRecommended()
    {
        Benchmark.Verdict(1.0, 1.0, null).Should().NotBe("recommended");
    }

    [Theory]
    [InlineData(new[] { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0 }, 0.5, 5.0)]
    [InlineData(new[] { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0 }, 0.95, 10.0)]
    public void Percentile_IsNearestRank(double[] values, double p, double expected)
    {
        Benchmark.Percentile(values, p).Should().Be(expected);
    }

    [Fact]
    public void Group_PutsNearMissesTogether_AndKeepsCustomCategoriesApart()
    {
        Benchmark.Group("20").Should().Be(Benchmark.Group("21"));
        Benchmark.Group("22").Should().NotBe(Benchmark.Group("21"));
        Benchmark.Group("C1001").Should().NotBe(Benchmark.Group("C1002"));
        Benchmark.Group(null).Should().Be("none");
    }
}
