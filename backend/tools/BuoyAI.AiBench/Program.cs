using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BuoyAI.AiBench;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Services.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

const string Usage = """
BuoyAI.AiBench: scores a model on transaction categorization with the app's real prompt and parser.

  dotnet run --project backend/tools/BuoyAI.AiBench -- --provider ollama --models qwen3.5:9b,llama3.2:3b

Options
  --provider <name>        ollama | openai | openrouter | gemini-compat | custom | gemini | fake  (default ollama)
  --models <a,b,c>         model names to score (required unless --list)
  --base-url <url>         override the provider's address (default for ollama: http://localhost:11434/v1)
  --api-key <key>          or set the Ai__ApiKey environment variable. Cloud providers cost money: nothing runs by itself.
  --rows <n>               score a stratified subset of n rows (default: all)
  --runs <n>               repeat each model n times to measure determinism (default 1)
  --concurrency <n>        calls in flight at once (default 6, what the app's batch uses)
  --timeout <seconds>      per call (default 600; local models can be slow)
  --seed <n>               dataset seed (default 42)
  --baseline-accuracy <x>  accuracy (0..1) of the reference model, e.g. Gemini on the same set, for the verdict
  --reasoning-effort <x>   none | low | medium | high. Use none for models that think by default (Qwen3, DeepSeek-R1)
  --price-in / --price-out USD per million tokens, for providers that do not report a cost
  --out <file.json>        also write the full results (scores, per-row outcomes, machine) as JSON
  --md <file.md>           also write the table as Markdown
  --list                   list the models the server offers and exit
  --dump-dataset <file>    write the labelled set as JSON and exit
  --dry-run                build the set and print the first prompt; call nothing
""";

var opts = Args.Parse(args);
if (opts.Has("help") || opts.Has("h")) { Console.WriteLine(Usage); return 0; }

var seed = opts.Int("seed", 42);
var dataset = SyntheticDataset.Build(seed);

if (opts.Get("dump-dataset") is { } dumpPath)
{
    var dump = dataset.Select(r => new
    {
        r.Id, r.Bucket, r.Expected,
        merchant = r.Transaction.MerchantName, description = r.Transaction.Description,
        amount = r.Transaction.Amount, type = r.Transaction.Type.ToString(), date = r.Transaction.Date.ToString("yyyy-MM-dd"),
        plaid = r.Transaction.PlaidCategoryDetailed,
    });
    File.WriteAllText(dumpPath, JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Wrote {dataset.Count} rows to {dumpPath}");
    return 0;
}

var rows = SyntheticDataset.Take(dataset, opts.Int("rows", dataset.Count));

if (opts.Has("dry-run"))
{
    Console.WriteLine($"{rows.Count} rows: " + string.Join(", ", rows.GroupBy(r => r.Bucket).Select(g => $"{g.Key} {g.Count()}")));
    var sample = rows.First(r => r.Bucket == SyntheticDataset.Split);
    Console.WriteLine($"\nSample prompt (row {sample.Id}, expected {sample.Expected}):\n");
    Console.WriteLine(BuoyAI.Infrastructure.Services.AICategorizationService.BuildCategorizationPrompt(
        sample.Transaction, sample.UserPatterns, SyntheticDataset.CustomCategories()));
    return 0;
}

var providerName = (opts.Get("provider") ?? "ollama").ToLowerInvariant();
var apiKey = opts.Get("api-key") ?? Environment.GetEnvironmentVariable("Ai__ApiKey") ?? "";
var timeout = TimeSpan.FromSeconds(opts.Int("timeout", 600));
using var http = new HttpClient { Timeout = timeout };

IAiProvider MakeProvider(string model)
{
    if (providerName == "fake") return new OracleProvider(dataset);
    var settings = new Dictionary<string, string?>
    {
        ["Ai:Provider"] = providerName,
        ["Ai:Model"] = model,
        ["Ai:ApiKey"] = apiKey,
        // Native Gemini reads its key from here, not Ai:ApiKey.
        ["Gemini:ApiKey"] = apiKey,
        ["Ai:BaseUrl"] = opts.Get("base-url"),
        ["Ai:ReasoningEffort"] = opts.Get("reasoning-effort"),
        ["Ai:PriceInputPerMillion"] = opts.Get("price-in"),
        ["Ai:PriceOutputPerMillion"] = opts.Get("price-out"),
    };
    var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    if (providerName == "gemini")
        return new GeminiNativeProvider(config, NullLogger<GeminiNativeProvider>.Instance, null);
    var options = AiProviderOptions.From(config, EditionInfo.From(config));
    if (options.SetupProblem is { } problem)
    {
        Console.Error.WriteLine($"Provider '{providerName}' is not set up: {problem}");
        Environment.Exit(2);
    }
    return new OpenAiCompatibleProvider(options, http, NullLogger<OpenAiCompatibleProvider>.Instance);
}

if (opts.Has("list"))
{
    var baseUrl = opts.Get("base-url") ?? (AiProviderOptions.PresetBaseUrls.TryGetValue(providerName, out var u) ? u : "");
    if (baseUrl == "") { Console.Error.WriteLine("Give --base-url."); return 2; }
    try
    {
        using var listRequest = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/models");
        if (apiKey != "") listRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        using var listing = await http.SendAsync(listRequest);
        Console.WriteLine(await listing.Content.ReadAsStringAsync());
        return listing.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException ex)
    {
        Console.Error.WriteLine($"Could not reach {baseUrl}: {ex.Message}");
        return 2;
    }
}

var models = (opts.Get("models") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (models.Length == 0 && providerName == "fake") models = ["fake-oracle"];
if (models.Length == 0) { Console.Error.WriteLine("Give --models (or --list). Run with --help for options."); return 2; }

var runCount = opts.Int("runs", 1);
var concurrency = opts.Int("concurrency", 6);
double? baseline = opts.Get("baseline-accuracy") is { } b ? double.Parse(b, CultureInfo.InvariantCulture) : null;

Console.WriteLine($"Scoring {models.Length} model(s) on {rows.Count} rows ({string.Join(", ", rows.GroupBy(r => r.Bucket).Select(g => $"{g.Key} {g.Count()}"))}), {runCount} run(s), {concurrency} at a time, provider {providerName}.");
if (providerName == "fake") Console.WriteLine("SELF-TEST: the fake oracle answers from the labels. These numbers say nothing about any real model.");

var scores = new List<ModelScore>();
var detail = new List<object>();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

foreach (var model in models)
{
    var provider = MakeProvider(model);
    var runs = new List<IReadOnlyList<RowOutcome>>();
    var wall = Stopwatch.StartNew();
    for (int run = 1; run <= runCount; run++)
    {
        var label = $"{model} run {run}/{runCount}";
        var last = -1;
        var outcomes = await Benchmark.RunOnceAsync(provider, model, rows, concurrency, (done, total) =>
        {
            var pct = done * 100 / total;
            if (pct / 10 != last / 10) { last = pct; Console.Error.Write($"\r  {label}: {done}/{total}   "); }
        }, cts.Token);
        Console.Error.WriteLine();
        runs.Add(outcomes);
        if (outcomes.Count > 0 && outcomes.All(o => o.Error))
        {
            Console.Error.WriteLine($"  Every call failed. First error: {outcomes[0].Reply}");
            break;
        }
    }
    wall.Stop();
    scores.Add(Benchmark.Score(model, runs, wall.Elapsed.TotalSeconds, baseline));
    detail.Add(new { model, runs = runs.Select(r => r.Select(o => new { o.RowId, o.Bucket, o.Expected, o.Predicted, o.Confidence, o.FormatOk, o.NotOffered, o.Error, o.Milliseconds, o.InputTokens, o.OutputTokens, o.Reply })) });
}

var markdown = Report.Markdown(scores, providerName, rows.Count);
Console.WriteLine();
Console.WriteLine(markdown);

if (opts.Get("md") is { } mdPath) File.WriteAllText(mdPath, markdown);
if (opts.Get("out") is { } outPath)
{
    var machine = new
    {
        os = RuntimeInformation.OSDescription,
        cpus = Environment.ProcessorCount,
        ramGb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024 / 1024, 1),
        note = "GPU is not detected here: note whether Ollama ran on GPU or CPU when you share these numbers.",
    };
    var payload = new
    {
        date = DateTime.UtcNow.ToString("u"), provider = providerName, selfTest = providerName == "fake",
        seed, rows = rows.Count, runs = runCount, concurrency, baselineAccuracy = baseline, machine, scores, detail,
    };
    File.WriteAllText(outPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Wrote {outPath}");
}

return scores.Any(s => s.Errors == s.Rows * s.Runs) ? 1 : 0;

internal sealed class Args
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public static Args Parse(string[] args)
    {
        var result = new Args();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var key = args[i][2..];
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
            result._values[key] = hasValue ? args[++i] : null;
        }
        return result;
    }

    public bool Has(string key) => _values.ContainsKey(key);
    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
    public int Int(string key, int fallback) => int.TryParse(Get(key), out var n) && n > 0 ? n : fallback;
}

internal static class Report
{
    public static string Markdown(IReadOnlyList<ModelScore> scores, string provider, int rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Categorization benchmark: provider {provider}, {rows} rows, {DateTime.UtcNow:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine("| Model | Valid format | Accuracy | Right group | Plain | Income | Split | Custom | Bad ids | Conf right / wrong | To review | p50 / p95 ms | Rows/min | In/out tokens | $ per 1k rows | Same answer | Verdict |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var s in scores)
        {
            string Bucket(string name) => s.Buckets.FirstOrDefault(x => x.Bucket == name) is { } x ? P(x.Accuracy) : "-";
            sb.AppendLine($"| {s.Model} | {P(s.FormatValidity)} | {P(s.Accuracy)} | {P(s.GroupAccuracy)} | {Bucket("plain")} | {Bucket("income")} | {Bucket("split")} | {Bucket("custom")} | " +
                          $"{P(s.HallucinatedShare)} | {s.MeanConfidenceWhenRight:F2} / {s.MeanConfidenceWhenWrong:F2} | {P(s.ShareToReview)} | " +
                          $"{s.LatencyP50Ms:F0} / {s.LatencyP95Ms:F0} | {s.RowsPerMinute:F1} | {s.MeanInputTokens:F0} / {s.MeanOutputTokens:F0} | " +
                          $"{s.CostPer1000Rows.ToString("F4", CultureInfo.InvariantCulture)} | {(s.Runs > 1 ? P(s.Determinism) : "n/a")} | {s.Verdict} |");
            if (s.Errors > 0) sb.AppendLine($"|  | {s.Errors} call(s) failed and count as wrong |||||||||||||||");
        }
        return sb.ToString();
    }

    private static string P(double v) => (v * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";
}
