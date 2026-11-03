using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.API.Controllers;

/// <summary>
/// "Can't find my bank": the app posts the bank a user was looking for after they leave
/// Plaid Link with <c>institution_not_found</c>. See <see cref="BankRequestService"/>.
/// </summary>
[Authorize]
[Route("api/plaid/bank-request")]
[BuoyAI.API.Filters.HostedOnly] // self-host: 404
public class BankRequestController : BaseController
{
  private readonly IBankRequestService _bankRequests;

  public BankRequestController(IBankRequestService bankRequests)
  {
    _bankRequests = bankRequests;
  }

  public class BankRequestBody
  {
    public string? BankName { get; set; }
    public string? Platform { get; set; }
  }

  [HttpPost]
  public async Task<IActionResult> Submit([FromBody] BankRequestBody body)
  {
    if (string.IsNullOrWhiteSpace(body?.BankName))
    {
      return BadRequest("Tell us which bank you were looking for.");
    }

    // A dropped request (daily limit) still answers 202: the user has done their part, and
    // telling them to stop asking would be worse than silently not emailing again.
    await _bankRequests.SubmitAsync(GetCurrentUserId(), body.BankName, body.Platform);
    return Accepted();
  }
}
