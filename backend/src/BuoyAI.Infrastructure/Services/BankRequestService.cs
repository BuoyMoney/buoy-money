using BuoyAI.Core.Utilities;
using System.Net;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// "Can't find my bank": when someone leaves Plaid Link because their bank wasn't in the list,
/// the app asks which bank it was and sends it here. The request goes to the support inbox,
/// which the founder reads, so he can reply to that person directly (and tell them if the bank
/// shows up later).
/// </summary>
/// <remarks>
/// Found 2026-09-25: of the people who started connecting a bank, the two most recent both
/// quit at Plaid's search with <c>institution_not_found</c>, and nothing recorded what they
/// were looking for. PostHog now records the search text too, but only for people who accepted
/// analytics; this reaches the founder either way.
/// </remarks>
public interface IBankRequestService
{
    /// <summary>
    /// Forwards one "which bank were you looking for" answer. Returns false when the request
    /// was dropped (empty, or the user already sent several today).
    /// </summary>
    Task<bool> SubmitAsync(int userId, string? bankName, string? platform);
}

public class BankRequestService : IBankRequestService
{
    /// <summary>Long enough for any bank name; anything longer is not a bank name.</summary>
    public const int MaxLength = 120;

    /// <summary>Per user per day. Each one is an email to a person, so it must not be a spam pipe.</summary>
    public const int DailyLimit = 3;

    private readonly IUserRepository _users;
    private readonly IEmailService _email;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BankRequestService> _logger;

    public BankRequestService(
        IUserRepository users,
        IEmailService email,
        IMemoryCache cache,
        IConfiguration configuration,
        ILogger<BankRequestService> logger)
    {
        _users = users;
        _email = email;
        _cache = cache;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<bool> SubmitAsync(int userId, string? bankName, string? platform)
    {
        var name = Clean(bankName);
        if (name.Length == 0)
        {
            return false;
        }

        var key = $"bank-request:{userId}:{DateTime.UtcNow:yyyyMMdd}";
        var count = _cache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1);
            return 0;
        });
        if (count >= DailyLimit)
        {
            _logger.LogWarning("Bank request from user {UserId} dropped: daily limit reached", userId);
            return false;
        }
        _cache.Set(key, count + 1, TimeSpan.FromDays(1));

        var user = await _users.GetByIdAsync(userId);
        var email = user?.Email ?? "(unknown)";
        var firstName = user?.FirstName ?? user?.Username ?? "";
        var device = string.IsNullOrWhiteSpace(platform) ? "unknown" : Clean(platform);

        // Structured log so requests can be counted even if the email is lost.
        _logger.LogInformation(
            "Bank request from user {UserId} on {Platform}: {BankName}", userId, device, name);

        var to = _configuration["Resend:ReplyToEmail"] ?? "support@buoy-ai.com";
        var subject = $"Bank request: {name}";
        var html = $@"<p>A user couldn't find their bank in Plaid and told us which one it was.</p>
<p><strong>Bank:</strong> {WebUtility.HtmlEncode(name)}<br>
<strong>User:</strong> {WebUtility.HtmlEncode(firstName)} &lt;{WebUtility.HtmlEncode(email)}&gt; (id {userId})<br>
<strong>Device:</strong> {WebUtility.HtmlEncode(device)}</p>
<p>Check whether Plaid supports it (dashboard, Institution status), then reply to them directly.</p>";
        var text = $@"A user couldn't find their bank in Plaid and told us which one it was.

Bank: {name}
User: {firstName} <{email}> (id {userId})
Device: {device}

Check whether Plaid supports it (dashboard, Institution status), then reply to them directly.";

        try
        {
            await _email.SendRawEmailAsync(to, subject, html, text);
        }
        catch (Exception ex)
        {
            // The log line above already recorded it; the user's side still succeeds.
            _logger.LogError(ex, "Bank request email failed for user {UserId}", userId);
        }

        return true;
    }

    /// <summary>Single line, trimmed, capped. Keeps a pasted paragraph or a header injection out of the subject.</summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var oneLine = string.Join(' ', value.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return SafeText.Truncate(oneLine, MaxLength);
    }
}
