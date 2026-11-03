using BuoyAI.Core.Interfaces;

namespace BuoyAI.AiBench;

/// <summary>
/// A stand-in "model" for checking the benchmark itself (<c>--provider fake</c>). It looks up the
/// row from the prompt and answers correctly most of the time, wrongly some of the time and in the
/// wrong format a little. Its scores say nothing about any real model; they only show that the
/// dataset, the real prompt, the real parser and the scoring fit together.
/// </summary>
internal sealed class OracleProvider : IAiProvider
{
    private readonly List<(string Description, string Amount, string Date, BenchRow Row)> _rows;
    private readonly double _accuracy;
    private readonly Random _rng;

    public OracleProvider(IEnumerable<BenchRow> rows, double accuracy = 0.85, int seed = 7)
    {
        _rows = rows.Select(r => (
            $"- Description: {r.Transaction.Description}\n",
            $"- Amount: ${Math.Abs(r.Transaction.Amount).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} ",
            $"- Date: {r.Transaction.Date:yyyy-MM-dd}\n", r)).ToList();
        _accuracy = accuracy;
        _rng = new Random(seed);
    }

    public string Id => "fake";
    public bool IsConfigured => true;
    public AiCapabilities Capabilities => new(true, false, false, false);
    public string ResolveModel(string requestedModel, string feature) => requestedModel;

    public Task<AiTextResult> GenerateTextAsync(AiTextRequest request, CancellationToken cancellationToken = default)
    {
        var prompt = request.Prompt.Replace("\r", ""); // the source file's line endings end up in the prompt
        var row = _rows.FirstOrDefault(r => prompt.Contains(r.Description) && prompt.Contains(r.Amount) && prompt.Contains(r.Date)).Row;
        double roll;
        lock (_rng) roll = _rng.NextDouble();

        string reply;
        if (row == null || roll > 0.97) reply = "I think this is a purchase.";
        else if (roll < _accuracy) reply = $"{row.Expected}:0.90:synthetic";
        else reply = row.Bucket == SyntheticDataset.Income ? "4:0.55:synthetic" : "24:0.55:synthetic";
        return Task.FromResult(new AiTextResult(reply, 600, 8, 0m));
    }
}
