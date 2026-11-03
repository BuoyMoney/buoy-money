using BuoyAI.Core.DTOs.Cloud;

namespace BuoyAI.Core.Interfaces;

/// <summary>Turns a verified BuoyAI Cloud event into an in-app notification (with its push copy) and an email.</summary>
public interface ICloudEventService
{
    Task<CloudEventOutcome> HandleAsync(CloudEvent cloudEvent, CancellationToken cancellationToken);
}
