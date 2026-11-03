namespace BuoyAI.Core.Exceptions;

/// <summary>
/// Thrown when a request violates an intentional business rule that the caller can correct
/// (e.g. "Insufficient funds", "categories must belong to the same budget"). These are expected,
/// client-facing validation failures that should surface as HTTP 400 with the rule message.
///
/// It derives from <see cref="InvalidOperationException"/> deliberately: existing controller
/// <c>catch (InvalidOperationException)</c> blocks and service catch-filters continue to treat it
/// as a domain error, while <see cref="System.InvalidOperationException"/> raised by the framework
/// or EF (which is NOT a domain rule) can now be distinguished and mapped to 500 by the error
/// middleware instead of leaking its message as a 400.
/// </summary>
public class DomainValidationException : InvalidOperationException
{
    public DomainValidationException(string message)
        : base(message)
    {
    }

    public DomainValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
