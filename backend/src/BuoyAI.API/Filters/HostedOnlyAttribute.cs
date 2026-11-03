using BuoyAI.Infrastructure.Configuration;

namespace BuoyAI.API.Filters;

/// <summary>
/// Marks a controller as part of the hosted service only (billing, store webhooks, direct Plaid,
/// marketing, admin broadcasts). On a self-hosted server every route of it answers 404, as if it
/// did not exist, whether or not the caller is signed in.
///
/// The attribute is endpoint metadata, read by <see cref="HostedOnlyExtensions.UseHostedOnlyGate"/>.
/// That gate runs before authentication and before MVC builds the controller, so the controller's
/// own dependencies (Stripe, Plaid) are never resolved on a server that has none of them.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class HostedOnlyAttribute : Attribute
{
}

public static class HostedOnlyExtensions
{
  /// <summary>
  /// Answers 404 for [HostedOnly] endpoints when the edition is self-host. Does nothing in hosted.
  /// Place after routing and before authentication.
  /// </summary>
  public static IApplicationBuilder UseHostedOnlyGate(this IApplicationBuilder app, EditionInfo edition)
  {
    if (!edition.IsSelfHost) return app;

    return app.Use(async (context, next) =>
    {
      if (context.GetEndpoint()?.Metadata.GetMetadata<HostedOnlyAttribute>() != null)
      {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
      }
      await next();
    });
  }
}
