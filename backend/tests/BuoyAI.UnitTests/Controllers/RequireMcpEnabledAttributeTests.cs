using BuoyAI.API.Filters;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuoyAI.UnitTests.Controllers;

/// <summary>
/// The BuoyAI Connect feature gate (RFC #782): when Mcp:Enabled is false the OAuth controller must
/// 404 so the surface is invisible until the flag is flipped.
/// </summary>
public class RequireMcpEnabledAttributeTests
{
  [Fact]
  public void OnResourceExecuting_FlagDisabled_ShortCircuitsWithNotFound()
  {
    var context = BuildContext(mcpEnabled: false);

    new RequireMcpEnabledAttribute().OnResourceExecuting(context);

    context.Result.Should().BeOfType<NotFoundResult>();
  }

  [Fact]
  public void OnResourceExecuting_FlagEnabled_DoesNotShortCircuit()
  {
    var context = BuildContext(mcpEnabled: true);

    new RequireMcpEnabledAttribute().OnResourceExecuting(context);

    context.Result.Should().BeNull();
  }

  private static ResourceExecutingContext BuildContext(bool mcpEnabled)
  {
    var config = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?>
      {
        ["Mcp:Enabled"] = mcpEnabled ? "true" : "false",
      })
      .Build();

    var services = new ServiceCollection()
      .AddSingleton<IConfiguration>(config)
      .BuildServiceProvider();

    var httpContext = new DefaultHttpContext { RequestServices = services };
    var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
    return new ResourceExecutingContext(
      actionContext,
      new List<IFilterMetadata>(),
      new List<IValueProviderFactory>());
  }
}
