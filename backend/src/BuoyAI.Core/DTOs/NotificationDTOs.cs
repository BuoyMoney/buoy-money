namespace BuoyAI.Core.DTOs;

public class NotificationDto
{
    public int Id { get; set; }
    public string Type { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string Message { get; set; } = null!;
    public string? ActionUrl { get; set; }
    public string? ActionText { get; set; }
    public int? RelatedConnectionId { get; set; }
    public string? InstitutionName { get; set; } // From related connection
    public bool IsRead { get; set; }
    public DateTime? ReadAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsArchived { get; set; }
}

public class CreateNotificationRequest
{
    public string Type { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string Message { get; set; } = null!;
    public string? ActionUrl { get; set; }
    public string? ActionText { get; set; }
    public int? RelatedConnectionId { get; set; }
}

public class MarkNotificationReadRequest
{
    public int NotificationId { get; set; }
}

public class NotificationSummaryDto
{
    public int TotalCount { get; set; }
    public int UnreadCount { get; set; }
    public List<NotificationDto> Recent { get; set; } = new();
}
