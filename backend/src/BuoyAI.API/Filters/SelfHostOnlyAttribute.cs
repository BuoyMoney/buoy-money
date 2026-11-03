using BuoyAI.Infrastructure.Configuration;

namespace BuoyAI.API.Filters;

/// <summary>
/// Marks a controller as part of the self-hosted server only (pairing with the BuoyAI Cloud). On the hosted service every
/// route of it answers 404, as if it did not exist. The mirror of <see cref="HostedOnlyAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SelfHostOnlyAttribute : Attribute
{
}

public static class SelfHostOnlyExtensions
{
  /// <summary>Answers 404 for [SelfHostOnly] endpoints when the edition is hosted. Does nothing on self-host.</summary>
  public static IApplicationBuilder UseSelfHostOnlyGate(this IApplicationBuilder app, EditionInfo edition)
  {
    if (edition.IsSelfHost) return app;

    return app.Use(async (context, next) =>
    {
      if (context.GetEndpoint()?.Metadata.GetMetadata<SelfHostOnlyAttribute>() != null)
      {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
      }
      await next();
    });
  }
}
