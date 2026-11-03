namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Interface for Gemini API interactions.
/// Abstracts the Gemini API client for use by insight generation and other AI services.
/// </summary>
public interface IGeminiApiClient
{
    /// <summary>
    /// True when a Gemini API key is configured. When false every generate call throws
    /// <see cref="BuoyAI.Core.Exceptions.AiUnavailableException"/>; callers can check this first to skip work.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Generate content using Gemini API with a simple prompt.
    /// </summary>
    /// <param name="prompt">The prompt to send to the model.</param>
    /// <param name="model">The model name to use (e.g., "gemini-2.0-flash").</param>
    /// <returns>The generated text response.</returns>
    /// <exception cref="BuoyAI.Core.Exceptions.AiUnavailableException">No API key is configured.</exception>
    /// <remarks>A failed call throws; there is no substitute reply.</remarks>
    Task<string> GenerateContentAsync(string prompt, string model);

    /// <summary>
    /// Generate content from a prompt plus files (images or PDFs) sent inline. Unlike the text-only
    /// call this never substitutes a canned reply: a failure throws, so callers can retry.
    /// </summary>
    Task<string> GenerateContentAsync(string prompt, string model, IReadOnlyList<(string MimeType, byte[] Data)> files) =>
        files.Count == 0
            ? GenerateContentAsync(prompt, model)
            : throw new NotSupportedException("This Gemini client can't send files");
}
