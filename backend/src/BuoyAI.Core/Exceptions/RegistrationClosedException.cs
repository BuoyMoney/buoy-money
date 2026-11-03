namespace BuoyAI.Core.Exceptions;

/// <summary>
/// A self-hosted server is closed to new accounts and the caller had no valid invite code.
/// Control flow, not a fault: the controller answers 403 with code "registration_closed".
/// </summary>
public class RegistrationClosedException : Exception
{
  public RegistrationClosedException(string message) : base(message) { }
}
