namespace BuoyAI.Core.Exceptions;

/// <summary>
/// Why a Google sign-up was refused. Each maps to a distinct HTTP status so the
/// difference is visible in metrics rather than collapsed into one code.
/// </summary>
public enum GoogleRegistrationRejection
{
    /// <summary>Disposable/throwaway email domain. The caller is not getting an account.</summary>
    BlockedDomain,

    /// <summary>Too many Google registrations in the last hour. Transient; retry later works.</summary>
    RateLimited,

    /// <summary>Self-host: registration is closed and no valid invite code came with the request.</summary>
    RegistrationClosed,
}

/// <summary>
/// Thrown when a Google sign-up is refused for a reason other than "no account exists".
/// </summary>
/// <remarks>
/// These previously returned a bare null from the service, which the controller mapped to
/// 401 "Invalid Google token" alongside a genuinely malformed or untrusted token. That made
/// three very different situations indistinguishable on the wire: a broken token audience
/// (an outage), a throwaway domain (working as designed), and a rate limit (transient
/// pressure). An alert on that endpoint could not tell an incident from normal traffic.
///
/// Deliberately does NOT cover the no-account case. That still throws
/// <see cref="GoogleAccountNotFoundException"/> and still answers 404, because the Flutter
/// client keys its redirect-to-register on that exact status and installed apps in the
/// field would break if it moved.
/// </remarks>
public class GoogleRegistrationRejectedException : Exception
{
    public GoogleRegistrationRejection Reason { get; }

    public GoogleRegistrationRejectedException(GoogleRegistrationRejection reason, string message)
        : base(message)
    {
        Reason = reason;
    }
}
