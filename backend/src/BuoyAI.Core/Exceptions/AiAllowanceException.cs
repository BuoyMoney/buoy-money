namespace BuoyAI.Core.Exceptions;

/// <summary>
/// BuoyAI Cloud refused an AI call because the allowance (or the customer's pay-as-you-go cap) is used up. It is a plain
/// answer, not an outage: the API returns 402 with this message and whether pay-as-you-go could be turned on.
/// </summary>
public class AiAllowanceException : AiUnavailableException
{
    public string Code { get; }
    public bool CanEnablePayg { get; }

    public AiAllowanceException(string code, string message, bool canEnablePayg)
        : base(message, null)
    {
        Code = code;
        CanEnablePayg = canEnablePayg;
    }
}
