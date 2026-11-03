using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Controller for managing AI memories - learned facts about users from conversations.
/// </summary>
[ApiController]
[Route("api/ai/memories")]
[Authorize]
public class AiMemoryController : BaseController
{
    private readonly IAiMemoryService _memoryService;
    private readonly IUserRepository _userRepository;
    private readonly ILogger<AiMemoryController> _logger;

    public AiMemoryController(
        IAiMemoryService memoryService,
        IUserRepository userRepository,
        ILogger<AiMemoryController> logger)
    {
        _memoryService = memoryService;
        _userRepository = userRepository;
        _logger = logger;
    }

    /// <summary>
    /// Get all AI memories for the current user.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetMemories()
    {
        var userId = GetCurrentUserId();

        // Check if user has memory enabled
        var user = await _userRepository.GetByIdAsync(userId);
        if (user?.AiMemoryEnabled != true)
        {
            return Ok(new GetMemoriesResponse
            {
                Memories = new List<MemoryDto>(),
                MemoryEnabled = false
            });
        }

        var memories = await _memoryService.GetUserMemoriesAsync(userId);

        return Ok(new GetMemoriesResponse
        {
            Memories = memories.Select(m => new MemoryDto
            {
                Id = m.Id,
                MemoryType = m.MemoryType,
                MemoryKey = m.MemoryKey,
                MemoryValue = m.MemoryValue,
                DisplayText = m.DisplayText,
                Confidence = m.Confidence,
                LearnedAt = m.LearnedAt,
                LastUsedAt = m.LastUsedAt,
                UsageCount = m.UsageCount,
                IsActive = m.IsActive,
                WasManuallyEdited = m.WasManuallyEdited
            }).ToList(),
            MemoryEnabled = true
        });
    }

    /// <summary>
    /// Update a specific AI memory.
    /// </summary>
    [HttpPut("{memoryId}")]
    public async Task<IActionResult> UpdateMemory(int memoryId, [FromBody] UpdateMemoryRequest request)
    {
        var userId = GetCurrentUserId();

        var updated = await _memoryService.UpdateMemoryAsync(
            userId,
            memoryId,
            request.Value,
            request.DisplayText);

        if (updated == null)
        {
            return NotFound(new { message = "Memory not found or you don't have access to it" });
        }

        _logger.LogInformation("User {UserId} updated memory {MemoryId}", userId, memoryId);

        return Ok(new MemoryDto
        {
            Id = updated.Id,
            MemoryType = updated.MemoryType,
            MemoryKey = updated.MemoryKey,
            MemoryValue = updated.MemoryValue,
            DisplayText = updated.DisplayText,
            Confidence = updated.Confidence,
            LearnedAt = updated.LearnedAt,
            LastUsedAt = updated.LastUsedAt,
            UsageCount = updated.UsageCount,
            IsActive = updated.IsActive,
            WasManuallyEdited = updated.WasManuallyEdited
        });
    }

    /// <summary>
    /// Deactivate (soft delete) a specific AI memory.
    /// </summary>
    [HttpPost("{memoryId}/deactivate")]
    public async Task<IActionResult> DeactivateMemory(int memoryId)
    {
        var userId = GetCurrentUserId();

        var success = await _memoryService.DeactivateMemoryAsync(userId, memoryId);

        if (!success)
        {
            return NotFound(new { message = "Memory not found or you don't have access to it" });
        }

        _logger.LogInformation("User {UserId} deactivated memory {MemoryId}", userId, memoryId);

        return Ok(new { message = "Memory deactivated successfully" });
    }

    /// <summary>
    /// Permanently delete a specific AI memory.
    /// </summary>
    [HttpDelete("{memoryId}")]
    public async Task<IActionResult> DeleteMemory(int memoryId)
    {
        var userId = GetCurrentUserId();

        var success = await _memoryService.DeleteMemoryAsync(userId, memoryId);

        if (!success)
        {
            return NotFound(new { message = "Memory not found or you don't have access to it" });
        }

        _logger.LogInformation("User {UserId} deleted memory {MemoryId}", userId, memoryId);

        return Ok(new { message = "Memory deleted successfully" });
    }

    /// <summary>
    /// Delete all AI memories for the current user.
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> DeleteAllMemories()
    {
        var userId = GetCurrentUserId();

        await _memoryService.DeleteAllUserMemoriesAsync(userId);

        _logger.LogInformation("User {UserId} deleted all AI memories", userId);

        return Ok(new { message = "All memories deleted successfully" });
    }
}

public class GetMemoriesResponse
{
    public List<MemoryDto> Memories { get; set; } = new();
    public bool MemoryEnabled { get; set; }
}

public class MemoryDto
{
    public int Id { get; set; }
    public string MemoryType { get; set; } = string.Empty;
    public string MemoryKey { get; set; } = string.Empty;
    public string MemoryValue { get; set; } = string.Empty;
    public string DisplayText { get; set; } = string.Empty;
    public decimal Confidence { get; set; }
    public DateTime LearnedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public int UsageCount { get; set; }
    public bool IsActive { get; set; }
    public bool WasManuallyEdited { get; set; }
}

public class UpdateMemoryRequest
{
    public string Value { get; set; } = string.Empty;
    public string DisplayText { get; set; } = string.Empty;
}
