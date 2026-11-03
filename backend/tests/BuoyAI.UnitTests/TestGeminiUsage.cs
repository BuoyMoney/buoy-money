using System.Diagnostics.Metrics;
using BuoyAI.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuoyAI.UnitTests;

/// <summary>
/// A real <see cref="GeminiUsageReporter"/> for tests that construct a Gemini-calling
/// service directly.
///
/// Real rather than a mock because it is a concrete class with no external dependencies:
/// it writes to an in-process Meter and, on the refusal path only, to Sentry. Tests that
/// use it never reach a Gemini call, so neither side effect fires. Mocking it would mean
/// making it virtual or extracting an interface purely to satisfy the test, which is a
/// worse trade than handing over the real thing.
/// </summary>
internal static class TestGeminiUsage
{
    public static GeminiUsageReporter Create() =>
        new(new BuoyAiMetrics(new TestMeterFactory()), NullLogger<GeminiUsageReporter>.Instance);

    private sealed class TestMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);
        public void Dispose() { }
    }
}
