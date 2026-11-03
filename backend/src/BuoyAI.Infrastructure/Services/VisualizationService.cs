using System.Text.Json;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.DTOs.Gamification;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Service for generating AI-powered financial visualizations.
/// Delegates to IAIService for the actual AI/tool calling, keeping the architecture DRY.
/// </summary>
public class VisualizationService : IVisualizationService
{
    private readonly BuoyAIDbContext _context;
    private readonly IOnboardingQuizService _quizService;
    private readonly IAIService _aiService;
    private readonly ILogger<VisualizationService> _logger;

    public VisualizationService(
        BuoyAIDbContext context,
        IOnboardingQuizService quizService,
        IAIService aiService,
        ILogger<VisualizationService> logger)
    {
        _context = context;
        _quizService = quizService;
        _aiService = aiService;
        _logger = logger;
    }

    public async Task<GenerateVisualizationResponse> GenerateVisualizationAsync(int userId, GenerateVisualizationRequest request)
    {
        try
        {
            _logger.LogInformation("Generating visualization for user {UserId}", userId);

            // Get user profile for personalization
            var userProfile = await _quizService.GetUserProfileForAIAsync(userId);
            
            // Get recent visualizations to avoid repetition
            var recentTitles = await GetRecentVisualizationTitlesAsync(userId, 10);
            
            // Build the query with visualization instructions
            var query = BuildVisualizationQuery(userProfile, recentTitles, request);
            
            // Use the existing AIService which handles all the tool calling logic
            var aiRequest = new AIQueryRequest
            {
                Query = query,
                ConversationHistory = null // Fresh context for each visualization
            };
            
            var aiResponse = await _aiService.ProcessQueryAsync(userId, aiRequest);
            
            if (!aiResponse.Success || string.IsNullOrEmpty(aiResponse.Response))
            {
                return new GenerateVisualizationResponse
                {
                    Success = false,
                    ErrorMessage = "Failed to generate visualization"
                };
            }
            
            // Parse the AI response to extract visualization spec
            var visualization = ParseVisualizationResponse(aiResponse.Response, userId);
            
            if (visualization == null)
            {
                _logger.LogWarning("Failed to parse visualization from AI response: {Response}", 
                    aiResponse.Response.Length > 500 ? aiResponse.Response[..500] : aiResponse.Response);
                return new GenerateVisualizationResponse
                {
                    Success = false,
                    ErrorMessage = "Failed to parse visualization from AI response"
                };
            }
            
            // Save to database
            var entity = new UserVisualization
            {
                UserId = userId,
                ChartType = visualization.ChartType,
                Title = visualization.Title,
                Message = visualization.Message,
                MotivationalMessage = visualization.MotivationalMessage,
                ChartData = JsonSerializer.Serialize(visualization.ChartData),
                ChartConfig = visualization.ChartConfig != null 
                    ? JsonSerializer.Serialize(visualization.ChartConfig) 
                    : null,
                Category = visualization.Category,
                Priority = visualization.Priority,
                Confidence = visualization.Confidence,
                GeneratedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            
            _context.UserVisualizations.Add(entity);
            await _context.SaveChangesAsync();
            
            visualization.Id = entity.Id;
            visualization.GeneratedAt = entity.GeneratedAt;
            
            _logger.LogInformation("Generated visualization {Id} for user {UserId}: {Title}", 
                entity.Id, userId, entity.Title);
            
            return new GenerateVisualizationResponse
            {
                Success = true,
                Visualization = visualization
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating visualization for user {UserId}", userId);
            return new GenerateVisualizationResponse
            {
                Success = false,
                ErrorMessage = "An error occurred while generating the visualization"
            };
        }
    }

    private string BuildVisualizationQuery(UserProfileForAIDto? profile, List<string> recentTitles, GenerateVisualizationRequest request)
    {
        var query = new System.Text.StringBuilder();
        
        query.AppendLine("Generate ONE insightful, personalized financial visualization chart for me.");
        query.AppendLine();
        query.AppendLine("Use your available tools to gather my financial data first, then create a meaningful visualization.");
        query.AppendLine();
        
        if (profile != null)
        {
            query.AppendLine("=== MY PROFILE ===");
            if (profile.PrimaryFinancialGoals?.Length > 0)
                query.AppendLine($"Financial Goals: {string.Join(", ", profile.PrimaryFinancialGoals)}");
            if (profile.BiggestChallenges?.Length > 0)
                query.AppendLine($"Challenges: {string.Join(", ", profile.BiggestChallenges)}");
            if (profile.SpendingPersonalities?.Length > 0)
                query.AppendLine($"Spending Style: {string.Join(", ", profile.SpendingPersonalities)}");
            query.AppendLine();
        }
        
        if (recentTitles.Count > 0)
        {
            query.AppendLine("=== RECENT VISUALIZATIONS (avoid repeating these) ===");
            foreach (var title in recentTitles.Take(5))
            {
                query.AppendLine($"  - {title}");
            }
            query.AppendLine();
        }
        
        if (!string.IsNullOrEmpty(request.CategoryHint))
        {
            query.AppendLine($"Focus on: {request.CategoryHint}");
        }
        
        if (!string.IsNullOrEmpty(request.FocusArea))
        {
            query.AppendLine($"Specific interest: {request.FocusArea}");
        }
        
        query.AppendLine();
        query.AppendLine("=== REQUIRED OUTPUT FORMAT ===");
        query.AppendLine("After gathering data, respond with ONLY a JSON object in this exact format (no other text):");
        query.AppendLine("```json");
        query.AppendLine("{");
        query.AppendLine("  \"chartType\": \"pie|bar|line|area|gauge|progress\",");
        query.AppendLine("  \"title\": \"Clear, descriptive title\",");
        query.AppendLine("  \"message\": \"2-3 sentence insight explaining what the chart shows and why it matters\",");
        query.AppendLine("  \"motivationalMessage\": \"Personalized encouragement based on the data\",");
        query.AppendLine("  \"category\": \"spending|savings|budget|income|trends|goals\",");
        query.AppendLine("  \"priority\": \"low|medium|high\",");
        query.AppendLine("  \"confidence\": 0.85,");
        query.AppendLine("  \"data\": [");
        query.AppendLine("    { \"name\": \"Category A\", \"value\": 500, \"color\": \"#4299E1\" },");
        query.AppendLine("    { \"name\": \"Category B\", \"value\": 300, \"color\": \"#48BB78\" }");
        query.AppendLine("  ],");
        query.AppendLine("  \"config\": {");
        query.AppendLine("    \"valuePrefix\": \"$\",");
        query.AppendLine("    \"showLegend\": true,");
        query.AppendLine("    \"animate\": true");
        query.AppendLine("  }");
        query.AppendLine("}");
        query.AppendLine("```");
        query.AppendLine();
        query.AppendLine("Color palette: #4299E1 (blue), #48BB78 (green), #ECC94B (yellow), #F56565 (red), #9F7AEA (purple), #38B2AC (teal), #ED8936 (orange), #ED64A6 (pink)");
        
        return query.ToString();
    }

    private UserVisualizationDto? ParseVisualizationResponse(string response, int userId)
    {
        try
        {
            // Extract JSON from response (might be wrapped in markdown code blocks)
            var jsonMatch = System.Text.RegularExpressions.Regex.Match(
                response, 
                @"```(?:json)?\s*(\{[\s\S]*?\})\s*```",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            
            var jsonStr = jsonMatch.Success ? jsonMatch.Groups[1].Value : response;
            
            // If still no valid JSON, try to find JSON object directly
            if (!jsonStr.TrimStart().StartsWith("{"))
            {
                var startIdx = response.IndexOf('{');
                var endIdx = response.LastIndexOf('}');
                if (startIdx >= 0 && endIdx > startIdx)
                {
                    jsonStr = response.Substring(startIdx, endIdx - startIdx + 1);
                }
            }
            
            // Try to parse as JSON
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;
            
            var dto = new UserVisualizationDto
            {
                ChartType = root.GetProperty("chartType").GetString() ?? "bar",
                Title = root.GetProperty("title").GetString() ?? "Financial Insight",
                Message = root.GetProperty("message").GetString() ?? "",
                Category = root.TryGetProperty("category", out var cat) ? cat.GetString() ?? "general" : "general",
                Priority = root.TryGetProperty("priority", out var pri) ? pri.GetString() ?? "medium" : "medium",
                Confidence = root.TryGetProperty("confidence", out var conf) ? conf.GetDouble() : 0.8,
                ChartData = new List<ChartDataPoint>()
            };
            
            if (root.TryGetProperty("motivationalMessage", out var motiv))
            {
                dto.MotivationalMessage = motiv.GetString();
            }
            
            // Parse data array
            if (root.TryGetProperty("data", out var dataArray) && dataArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dataArray.EnumerateArray())
                {
                    dto.ChartData.Add(new ChartDataPoint
                    {
                        Name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        Value = item.TryGetProperty("value", out var v) ? v.GetDecimal() : 0,
                        Color = item.TryGetProperty("color", out var c) ? c.GetString() : null,
                        Label = item.TryGetProperty("label", out var l) ? l.GetString() : null
                    });
                }
            }
            
            // Parse config
            if (root.TryGetProperty("config", out var configEl))
            {
                dto.ChartConfig = new ChartConfig
                {
                    ValuePrefix = configEl.TryGetProperty("valuePrefix", out var vp) ? vp.GetString() : null,
                    ValueSuffix = configEl.TryGetProperty("valueSuffix", out var vs) ? vs.GetString() : null,
                    ShowLegend = configEl.TryGetProperty("showLegend", out var sl) && sl.GetBoolean(),
                    Animate = configEl.TryGetProperty("animate", out var an) && an.GetBoolean(),
                    XAxisLabel = configEl.TryGetProperty("xAxisLabel", out var xl) ? xl.GetString() : null,
                    YAxisLabel = configEl.TryGetProperty("yAxisLabel", out var yl) ? yl.GetString() : null
                };
            }
            
            return dto;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse visualization response");
            return null;
        }
    }

    public async Task<VisualizationListResponse> GetVisualizationsAsync(int userId, GetVisualizationsRequest request)
    {
        var query = _context.UserVisualizations
            .Where(v => v.UserId == userId);
        
        if (!request.IncludeDismissed)
        {
            query = query.Where(v => !v.IsDismissed);
        }
        
        if (request.StarredOnly == true)
        {
            query = query.Where(v => v.IsStarred);
        }
        
        if (!string.IsNullOrEmpty(request.Category))
        {
            query = query.Where(v => v.Category == request.Category);
        }
        
        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize);
        
        var visualizations = await query
            .OrderByDescending(v => v.IsStarred)
            .ThenByDescending(v => v.GeneratedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(v => new UserVisualizationDto
            {
                Id = v.Id,
                ChartType = v.ChartType,
                Title = v.Title,
                Message = v.Message,
                MotivationalMessage = v.MotivationalMessage,
                ChartData = JsonSerializer.Deserialize<List<ChartDataPoint>>(v.ChartData) ?? new(),
                ChartConfig = string.IsNullOrEmpty(v.ChartConfig) 
                    ? null 
                    : JsonSerializer.Deserialize<ChartConfig>(v.ChartConfig),
                Category = v.Category,
                Priority = v.Priority,
                Confidence = v.Confidence,
                IsStarred = v.IsStarred,
                IsDismissed = v.IsDismissed,
                ViewCount = v.ViewCount,
                LastViewedAt = v.LastViewedAt,
                GeneratedAt = v.GeneratedAt
            })
            .ToListAsync();
        
        return new VisualizationListResponse
        {
            Visualizations = visualizations,
            TotalCount = totalCount,
            Page = request.Page,
            PageSize = request.PageSize,
            TotalPages = totalPages
        };
    }

    public async Task<UserVisualizationDto?> GetVisualizationByIdAsync(int userId, int visualizationId)
    {
        var entity = await _context.UserVisualizations
            .FirstOrDefaultAsync(v => v.Id == visualizationId && v.UserId == userId);
        
        if (entity == null) return null;
        
        return new UserVisualizationDto
        {
            Id = entity.Id,
            ChartType = entity.ChartType,
            Title = entity.Title,
            Message = entity.Message,
            MotivationalMessage = entity.MotivationalMessage,
            ChartData = JsonSerializer.Deserialize<List<ChartDataPoint>>(entity.ChartData) ?? new(),
            ChartConfig = string.IsNullOrEmpty(entity.ChartConfig) 
                ? null 
                : JsonSerializer.Deserialize<ChartConfig>(entity.ChartConfig),
            Category = entity.Category,
            Priority = entity.Priority,
            Confidence = entity.Confidence,
            IsStarred = entity.IsStarred,
            IsDismissed = entity.IsDismissed,
            ViewCount = entity.ViewCount,
            LastViewedAt = entity.LastViewedAt,
            GeneratedAt = entity.GeneratedAt
        };
    }

    public async Task<bool> ToggleStarAsync(int userId, int visualizationId)
    {
        var entity = await _context.UserVisualizations
            .FirstOrDefaultAsync(v => v.Id == visualizationId && v.UserId == userId);
        
        if (entity == null) return false;
        
        entity.IsStarred = !entity.IsStarred;
        entity.UpdatedAt = DateTime.UtcNow;
        
        await _context.SaveChangesAsync();
        return entity.IsStarred;
    }

    public async Task<bool> DismissVisualizationAsync(int userId, int visualizationId)
    {
        var entity = await _context.UserVisualizations
            .FirstOrDefaultAsync(v => v.Id == visualizationId && v.UserId == userId);
        
        if (entity == null) return false;
        
        entity.IsDismissed = true;
        entity.UpdatedAt = DateTime.UtcNow;
        
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task RecordViewAsync(int userId, int visualizationId)
    {
        var entity = await _context.UserVisualizations
            .FirstOrDefaultAsync(v => v.Id == visualizationId && v.UserId == userId);
        
        if (entity != null)
        {
            entity.ViewCount++;
            entity.LastViewedAt = DateTime.UtcNow;
            entity.UpdatedAt = DateTime.UtcNow;
            
            await _context.SaveChangesAsync();
        }
    }

    public async Task<List<string>> GetRecentVisualizationTitlesAsync(int userId, int count = 10)
    {
        return await _context.UserVisualizations
            .Where(v => v.UserId == userId)
            .OrderByDescending(v => v.GeneratedAt)
            .Take(count)
            .Select(v => v.Title)
            .ToListAsync();
    }
}
