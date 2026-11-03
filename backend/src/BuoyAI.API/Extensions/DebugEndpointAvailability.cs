using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using BuoyAI.API.Controllers;

namespace BuoyAI.API.Extensions;

/// <summary>
/// Decides whether the debug routes (<c>/api/debug/*</c>: token minting, diagnostics) exist at all.
///
/// Why not <c>#if DEBUG</c> on the controller: staging runs a Release build (the Dockerfiles build
/// <c>-c Release</c> and staging sets ASPNETCORE_ENVIRONMENT=Staging) and its E2E harness and
/// operators mint tokens with the X-Debug-Key header. A compile-time removal would break that, so
/// the rule is enforced at startup instead, and the routes are not registered when it says no:
///   Production                     never
///   Development, Debug build       yes (no key; local dev and the fleet run Debug builds)
///   Development, Release build     only when Debug:ApiKey is set (a Release image run with
///                                  ASPNETCORE_ENVIRONMENT=Development is not a dev box)
///   any other environment          only when Debug:ApiKey is set (staging)
/// </summary>
public static class DebugEndpointAvailability
{
  /// <summary>True only in Debug builds: the one place a debug call may skip the X-Debug-Key.</summary>
  public const bool KeylessDevelopmentAllowed =
#if DEBUG
    true;
#else
    false;
#endif

  public static bool IsAvailable(IHostEnvironment environment, IConfiguration configuration)
  {
    if (environment.IsProduction()) return false;
    if (environment.IsDevelopment() && KeylessDevelopmentAllowed) return true;
    return !string.IsNullOrEmpty(configuration["Debug:ApiKey"]);
  }

  public static IMvcBuilder AddDebugEndpointGate(
    this IMvcBuilder mvc, IHostEnvironment environment, IConfiguration configuration)
  {
    return mvc.ConfigureApplicationPartManager(manager =>
      manager.FeatureProviders.Add(new DebugControllerGate(IsAvailable(environment, configuration))));
  }

  private sealed class DebugControllerGate : IApplicationFeatureProvider<ControllerFeature>
  {
    private readonly bool _available;
    public DebugControllerGate(bool available) => _available = available;

    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
      if (_available) return;
      var debug = feature.Controllers.Where(t => t.AsType() == typeof(DebugController)).ToList();
      foreach (var type in debug) feature.Controllers.Remove(type);
    }
  }
}
