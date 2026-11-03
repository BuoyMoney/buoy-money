namespace BuoyAI.Core.Exceptions;

/// <summary>
/// A single sign-on attempt was refused. <see cref="Code"/> is a short machine code the app maps to
/// its own message (it travels in the redirect back as <c>?error=</c>): <c>no_account</c>,
/// <c>registration_closed</c>, <c>email_unverified</c>, <c>invalid_response</c>, <c>expired</c>,
/// <c>provider_unavailable</c>, <c>denied</c>.
/// </summary>
public class OidcLoginException : Exception
{
  public string Code { get; }

  public OidcLoginException(string code, string message, Exception? inner = null) : base(message, inner)
  {
    Code = code;
  }
}
