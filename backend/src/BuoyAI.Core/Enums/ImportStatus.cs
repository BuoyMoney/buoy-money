namespace BuoyAI.Core.Enums;

public enum ImportStatus
{
    Pending = 0,
    InProgress = 1,
    Completed = 2,
    Failed = 3,
    QuickSyncComplete = 4  // Quick 30-day sync is done, historical import still in progress
}
