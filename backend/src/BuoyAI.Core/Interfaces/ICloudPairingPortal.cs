namespace BuoyAI.Core.Interfaces;

/// <summary>A self-hosted server on the user's Cloud account, as the app shows it.</summary>
/// <param name="Status"><c>awaiting_confirmation</c>, <c>linked</c> or <c>revoked</c>.</param>
public record CloudServer(
    Guid Id,
    string Name,
    string? ServerVersion,
    DateTime PairedAt,
    DateTime? ConfirmedAt,
    DateTime? LastSeenAt,
    DateTime? RevokedAt,
    string Status);

/// <summary>A one-time code the user types into their own server to link it.</summary>
public record CloudPairingCode(string Code, DateTime ExpiresAt);

/// <summary>
/// A server asking to be linked by code (RFC 8628 style): the server shows the code, the owner types it on the Cloud
/// portal and approves. <paramref name="Status"/> is <c>pending</c>, <c>approved</c>, <c>denied</c> or <c>expired</c>.
/// </summary>
public record CloudLinkSession(
    string UserCode,
    string Name,
    string? ServerVersion,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    string Status);

/// <summary>
/// The Cloud pairing API, called as the account owner. <paramref name="ownerId"/> is the Cloud account id
/// (the user's Stripe customer id). <c>mfa</c> must be true only when the request passed step-up MFA.
/// </summary>
public interface ICloudPairingPortal
{
    bool IsConfigured { get; }
    Task<IReadOnlyList<CloudServer>> ListServersAsync(string ownerId, CancellationToken ct = default);
    /// <param name="autoConfirm">
    /// The owner approved the server in the browser (the link flow), so the server that spends this code starts
    /// confirmed. Only ever true together with <paramref name="mfa"/>.
    /// </param>
    Task<CloudPairingCode> CreateCodeAsync(string ownerId, bool mfa, bool autoConfirm = false, CancellationToken ct = default);
    Task ConfirmAsync(string ownerId, Guid serverId, bool mfa, CancellationToken ct = default);
    Task RevokeAsync(string ownerId, Guid serverId, CancellationToken ct = default);

    /// <summary>The server waiting behind <paramref name="userCode"/> (already normalised, <c>XXXX-XXXX</c>).</summary>
    Task<CloudLinkSession> GetLinkSessionAsync(string ownerId, string userCode, CancellationToken ct = default);

    /// <summary>
    /// Links the waiting server to this owner and returns the session as it now stands. <paramref name="mfa"/> must be
    /// true (step-up passed). Approving twice is fine; a session another owner (or a deny) already decided is a 409
    /// <c>already_decided</c>.
    /// </summary>
    Task<CloudLinkSession> ApproveLinkSessionAsync(string ownerId, string userCode, bool mfa, CancellationToken ct = default);

    /// <summary>Refuses the waiting server (it stops polling and shows "denied") and returns the session.</summary>
    Task<CloudLinkSession> DenyLinkSessionAsync(string ownerId, string userCode, CancellationToken ct = default);
}

/// <summary>The Cloud refused or could not be reached. <see cref="Code"/> is stable, the message is safe to show.</summary>
public class CloudPortalException : Exception
{
    public string Code { get; }
    public int StatusCode { get; }

    public CloudPortalException(string code, string message, int statusCode) : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }
}
