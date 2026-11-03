using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The AI-insight queue is the seam that keeps slow Gemini calls off the request thread.
/// These lock its contract: valid jobs are readable by the background consumer, and junk
/// (no user / blank context) is dropped so the worker never churns on it.
/// </summary>
public class AiInsightQueueTests
{
    private static AiInsightQueue NewQueue() => new(NullLogger<AiInsightQueue>.Instance);

    [Fact]
    public void Enqueue_ValidJob_IsReadableByConsumer()
    {
        var queue = NewQueue();

        queue.Enqueue(42, "dashboard", 5);

        queue.Reader.TryRead(out var job).Should().BeTrue();
        job.UserId.Should().Be(42);
        job.Context.Should().Be("dashboard");
        job.MaxInsights.Should().Be(5);
    }

    [Theory]
    [InlineData(0, "dashboard")]   // no authenticated user
    [InlineData(-1, "dashboard")]  // invalid user id
    [InlineData(42, "")]           // blank context
    [InlineData(42, "   ")]        // whitespace context
    public void Enqueue_InvalidJob_IsDropped(int userId, string context)
    {
        var queue = NewQueue();

        queue.Enqueue(userId, context, 5);

        queue.Reader.TryRead(out _).Should().BeFalse();
    }
}
