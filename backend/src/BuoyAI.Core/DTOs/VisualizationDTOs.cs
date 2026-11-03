using BuoyAI.Core.DTOs.Gamification;

namespace BuoyAI.Core.DTOs;

/// <summary>
/// DTO for returning a user visualization
/// </summary>
public class UserVisualizationDto
{
    public int Id { get; set; }
    public string ChartType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? MotivationalMessage { get; set; }
    public List<ChartDataPoint> ChartData { get; set; } = new();
    public ChartConfig? ChartConfig { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Priority { get; set; } = "medium";
    public double Confidence { get; set; }
    public bool IsStarred { get; set; }
    public bool IsDismissed { get; set; }
    public int ViewCount { get; set; }
    public DateTime? LastViewedAt { get; set; }
    public DateTime GeneratedAt { get; set; }
}

/// <summary>
/// Request to generate a new visualization
/// </summary>
public class GenerateVisualizationRequest
{
    /// <summary>
    /// Optional category hint: spending, savings, budget, income, trends, goals
    /// If not specified, AI will choose based on user profile
    /// </summary>
    public string? CategoryHint { get; set; }
    
    /// <summary>
    /// Optional specific question or focus area
    /// </summary>
    public string? FocusArea { get; set; }
}

/// <summary>
/// Response from visualization generation
/// </summary>
public class GenerateVisualizationResponse
{
    public bool Success { get; set; }
    public UserVisualizationDto? Visualization { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Request to get visualization history
/// </summary>
public class GetVisualizationsRequest
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public bool? StarredOnly { get; set; }
    public string? Category { get; set; }
    public bool IncludeDismissed { get; set; } = false;
}

/// <summary>
/// Paginated visualization response
/// </summary>
public class VisualizationListResponse
{
    public List<UserVisualizationDto> Visualizations { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
}
