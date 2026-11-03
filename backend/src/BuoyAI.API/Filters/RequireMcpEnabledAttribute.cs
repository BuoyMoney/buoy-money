using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;

namespace BuoyAI.API.Filters;

/// <summary>
/// Feature gate for BuoyAI Connect (RFC #782). When <c>Mcp:Enabled</c> is false, every action on
/// the decorated controller returns 404 — the OAuth surface is invisible until the flag is flipped.
/// A resource filter runs before model binding/auth, so a disabled deployment reveals nothing.
/// </summary>
public sealed class RequireMcpEnabledAttribute : Attribute, IResourceFilter
{
  public void OnResourceExecuting(ResourceExecutingContext context)
  {
    var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
    if (!configuration.GetValue<bool>("Mcp:Enabled"))
      context.Result = new NotFoundResult();
  }

  public void OnResourceExecuted(ResourceExecutedContext context)
  {
    // no-op
  }
}
