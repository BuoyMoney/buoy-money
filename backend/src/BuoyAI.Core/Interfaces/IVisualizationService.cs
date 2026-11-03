using BuoyAI.Core.DTOs;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Service for AI-powered visualization generation and management.
/// Uses chat-based AI with MCP tools to gather context and generate insights.
/// </summary>
public interface IVisualizationService
{
    /// <summary>
    /// Generate a new AI visualization for the user.
    /// The AI will use MCP tools to gather relevant context and create a personalized chart.
    /// </summary>
    Task<GenerateVisualizationResponse> GenerateVisualizationAsync(int userId, GenerateVisualizationRequest request);
    
    /// <summary>
    /// Get visualization history for a user with pagination and filtering.
    /// </summary>
    Task<VisualizationListResponse> GetVisualizationsAsync(int userId, GetVisualizationsRequest request);
    
    /// <summary>
    /// Get a specific visualization by ID.
    /// </summary>
    Task<UserVisualizationDto?> GetVisualizationByIdAsync(int userId, int visualizationId);
    
    /// <summary>
    /// Toggle starred status for a visualization.
    /// </summary>
    Task<bool> ToggleStarAsync(int userId, int visualizationId);
    
    /// <summary>
    /// Dismiss a visualization (hide from default list).
    /// </summary>
    Task<bool> DismissVisualizationAsync(int userId, int visualizationId);
    
    /// <summary>
    /// Record a view of a visualization.
    /// </summary>
    Task RecordViewAsync(int userId, int visualizationId);
    
    /// <summary>
    /// Get recent visualization history (for AI to avoid repetition).
    /// </summary>
    Task<List<string>> GetRecentVisualizationTitlesAsync(int userId, int count = 10);
}
