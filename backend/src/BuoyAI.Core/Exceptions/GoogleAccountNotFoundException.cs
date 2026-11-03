namespace BuoyAI.Core.Exceptions;

/// <summary>
/// Thrown when a Google sign-in is attempted from the login flow but no account
/// exists for the verified Google email. Lets the controller respond with a 404
/// so the client can redirect the user to the registration flow instead of
/// silently auto-creating an account.
/// </summary>
public class GoogleAccountNotFoundException : Exception
{
    /// <summary>
    /// The verified Google email that has no associated account.
    /// </summary>
    public string Email { get; }

    public GoogleAccountNotFoundException(string email)
        : base("No account exists for this Google email. Please sign up first.")
    {
        Email = email;
    }
}
