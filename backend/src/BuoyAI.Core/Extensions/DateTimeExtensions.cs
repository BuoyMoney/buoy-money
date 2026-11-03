namespace BuoyAI.Core.Extensions;

/// <summary>
/// Extension methods for DateTime handling, particularly UTC conversion.
/// Centralizes date handling logic to avoid duplication across controllers.
/// </summary>
public static class DateTimeExtensions
{
    /// <summary>
    /// Converts a DateTime to UTC, handling different DateTimeKind values appropriately.
    /// If the DateTime is unspecified, it's treated as local time and converted to UTC.
    /// </summary>
    /// <param name="dateTime">The DateTime to convert</param>
    /// <returns>The DateTime in UTC</returns>
    public static DateTime ToUtcSafe(this DateTime dateTime)
    {
        return dateTime.Kind switch
        {
            DateTimeKind.Utc => dateTime,
            DateTimeKind.Local => dateTime.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(dateTime, DateTimeKind.Local).ToUniversalTime(),
            _ => dateTime.ToUniversalTime()
        };
    }

    /// <summary>
    /// Converts a nullable DateTime to UTC, returning null if input is null.
    /// Handles different DateTimeKind values appropriately.
    /// </summary>
    /// <param name="dateTime">The nullable DateTime to convert</param>
    /// <returns>The DateTime in UTC, or null if input was null</returns>
    public static DateTime? ToUtcSafe(this DateTime? dateTime)
    {
        return dateTime?.ToUtcSafe();
    }

    /// <summary>
    /// Gets an effective date range with UTC conversion and default values.
    /// Commonly used pattern in controllers for date range queries.
    /// </summary>
    /// <param name="startDate">Optional start date from query</param>
    /// <param name="endDate">Optional end date from query</param>
    /// <param name="defaultDaysBack">Days to go back for default start date (default: 30)</param>
    /// <returns>Tuple of (effectiveStartDate, effectiveEndDate) in UTC</returns>
    public static (DateTime StartDate, DateTime EndDate) GetEffectiveDateRange(
        DateTime? startDate, 
        DateTime? endDate, 
        int defaultDaysBack = 30)
    {
        var effectiveStartDate = startDate?.ToUtcSafe() ?? DateTime.UtcNow.AddDays(-defaultDaysBack);
        var effectiveEndDate = endDate?.ToUtcSafe() ?? DateTime.UtcNow;
        
        return (effectiveStartDate, effectiveEndDate);
    }

    /// <summary>
    /// Validates that start date is before or equal to end date.
    /// </summary>
    /// <param name="startDate">Start date</param>
    /// <param name="endDate">End date</param>
    /// <returns>True if the date range is valid</returns>
    public static bool IsValidDateRange(DateTime startDate, DateTime endDate)
    {
        return startDate <= endDate;
    }
}
