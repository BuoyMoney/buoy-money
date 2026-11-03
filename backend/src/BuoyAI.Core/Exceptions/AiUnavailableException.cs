namespace BuoyAI.Core.Exceptions;

/// <summary>
/// Thrown when an AI feature is asked to run but no Gemini API key is configured. Callers that
/// can degrade (insights, wrapped text) catch it and return nothing; API endpoints that cannot
/// map it to 503 with this message. Derives from <see cref="InvalidOperationException"/> so code
/// that already catches that keeps working.
/// </summary>
public class AiUnavailableException : InvalidOperationException
{
    public AiUnavailableException(string message, Exception? inner)
        : base(message, inner)
    {
    }

    public AiUnavailableException()
        : base("AI features are not available: no Gemini API key is configured on this server.")
    {
    }
}
