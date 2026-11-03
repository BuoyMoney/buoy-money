using BuoyAI.Core.Utilities;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BuoyAI.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuoyAI.Infrastructure.Services.SimpleFin;

/// <summary>
/// The SimpleFIN protocol (https://www.simplefin.org/protocol.html) and nothing else: turn a
/// pasted setup token into an access URL, and read <c>/accounts</c> with it. No database, no
/// BuoyAI types, so it is easy to test with a fake HTTP handler.
///
/// The setup token and the access URL are secrets. They are never logged and never put in an
/// exception message.
/// </summary>
public class SimpleFinClient
{
    public const string HttpClientName = "simplefin";

    private static readonly Regex Url = new(@"https?://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AuthText = new(
        @"authenticat|re-?auth|log ?in|credential|password", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IHttpClientFactory _httpClients;
    private readonly SimpleFinOptions _options;
    private readonly ILogger<SimpleFinClient> _logger;

    public SimpleFinClient(
        IHttpClientFactory httpClients,
        IOptions<SimpleFinOptions> options,
        ILogger<SimpleFinClient> logger)
    {
        _httpClients = httpClients;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Claims a setup token: decodes it to the claim URL, POSTs to it once and returns the access URL
    /// (<c>https://user:pass@host/simplefin</c>). A setup token works once. Throws
    /// <see cref="SimpleFinException"/> with a user-safe message when the token is malformed, points
    /// somewhere we do not allow, or the bridge refuses it.
    /// </summary>
    public async Task<string> ClaimAsync(string setupToken, CancellationToken cancellationToken = default)
    {
        var claimUri = DecodeSetupToken(setupToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, claimUri) { Content = new ByteArrayContent([]) };
        using var response = await SendAsync(request, cancellationToken);

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            throw new SimpleFinException(
                "SimpleFIN did not accept that setup token. A token works once, so make a new one and try again.");
        }
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("SimpleFIN claim returned HTTP {Status}", (int)response.StatusCode);
            throw new SimpleFinException($"SimpleFIN could not be reached (HTTP {(int)response.StatusCode}). Try again later.");
        }

        var accessUrl = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        ParseAccessUrl(accessUrl); // checks scheme, host and credentials; throws if wrong
        return accessUrl;
    }

    /// <summary>
    /// Reads <c>/accounts</c> for the window. SimpleFIN limits one request to 90 days; split longer
    /// windows with <see cref="SplitWindow"/> and call this once per slice.
    /// </summary>
    public async Task<SimpleFinAccountSet> GetAccountsAsync(
        string accessUrl, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
    {
        var (baseUri, user, password) = ParseAccessUrl(accessUrl);

        var query = string.Create(CultureInfo.InvariantCulture,
            $"start-date={ToUnix(fromUtc)}&end-date={ToUnix(toUtc)}");
        var uri = new Uri($"{baseUri.AbsoluteUri.TrimEnd('/')}/accounts?{query}");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

        using var response = await SendAsync(request, cancellationToken);

        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
            case HttpStatusCode.Forbidden:
                throw new SimpleFinAuthException(
                    "SimpleFIN no longer accepts this connection. Make a new setup token and paste it again.");
            case HttpStatusCode.PaymentRequired:
                throw new SimpleFinAuthException(
                    "SimpleFIN says the bridge subscription needs attention. Renew it, then make a new setup token.");
            case HttpStatusCode.TooManyRequests:
                throw new SimpleFinException("SimpleFIN asked us to slow down. It will be tried again later.");
        }
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("SimpleFIN accounts returned HTTP {Status}", (int)response.StatusCode);
            throw new SimpleFinException($"SimpleFIN returned an error (HTTP {(int)response.StatusCode}).");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return Parse(json);
    }

    /// <summary>Splits [from, to] into consecutive windows of at most 90 days, oldest first.</summary>
    public static IReadOnlyList<(DateTime From, DateTime To)> SplitWindow(DateTime fromUtc, DateTime toUtc)
    {
        const int maxDays = 90;
        var slices = new List<(DateTime, DateTime)>();
        if (toUtc <= fromUtc)
        {
            return slices;
        }

        var start = fromUtc;
        while (start < toUtc)
        {
            var end = start.AddDays(maxDays);
            if (end > toUtc)
            {
                end = toUtc;
            }
            slices.Add((start, end));
            start = end;
        }
        return slices;
    }

    /// <summary>
    /// A setup token is the Base64 of the claim URL. Accepts only an https URL with no embedded
    /// credentials, on the default port, on an allowed host.
    /// </summary>
    internal Uri DecodeSetupToken(string setupToken)
    {
        const string bad = "That does not look like a SimpleFIN setup token. Copy the whole token from the bridge.";
        if (string.IsNullOrWhiteSpace(setupToken))
        {
            throw new SimpleFinException(bad);
        }

        string decoded;
        try
        {
            var cleaned = new string(setupToken.Where(c => !char.IsWhiteSpace(c)).ToArray())
                .Replace('-', '+').Replace('_', '/');
            cleaned = cleaned.PadRight(cleaned.Length + (4 - cleaned.Length % 4) % 4, '=');
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cleaned)).Trim();
        }
        catch (FormatException)
        {
            throw new SimpleFinException(bad);
        }

        if (!Uri.TryCreate(decoded, UriKind.Absolute, out var uri))
        {
            throw new SimpleFinException(bad);
        }
        RequireAllowed(uri, "setup token");
        // The telemetry filters recognise a claim by this path, so anything else could leak the one-time token.
        if (!uri.AbsolutePath.Contains("/simplefin/claim", StringComparison.OrdinalIgnoreCase))
        {
            throw new SimpleFinException(bad);
        }
        return uri;
    }

    /// <summary>Splits an access URL into the base to call, the user name and the password.</summary>
    internal (Uri BaseUri, string User, string Password) ParseAccessUrl(string accessUrl)
    {
        if (!Uri.TryCreate(accessUrl, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new SimpleFinException("SimpleFIN returned an access address we could not use.");
        }
        RequireAllowed(uri, "access address", allowUserInfo: true);

        var parts = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(parts[0]);
        var password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        var baseUri = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri;
        return (baseUri, user, password);
    }

    internal bool IsAllowedHost(string host)
    {
        var allowed = _options.AllowedHosts
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowed.Any(a =>
            host.Equals(a, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + a, StringComparison.OrdinalIgnoreCase));
    }

    private void RequireAllowed(Uri uri, string what, bool allowUserInfo = false)
    {
        var ok = uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort
            && (allowUserInfo || string.IsNullOrEmpty(uri.UserInfo))
            && IsAllowedHost(uri.Host);
        if (!ok)
        {
            // Say what is wrong, never what was pasted.
            throw new SimpleFinException(
                $"That {what} points somewhere Buoy Money is not set up to call. Only {_options.AllowedHosts} over https is allowed.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var http = _httpClients.CreateClient(HttpClientName);
        try
        {
            return await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("SimpleFIN request failed: {Error}", ex.GetType().Name);
            throw new SimpleFinException("SimpleFIN could not be reached. Try again later.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SimpleFinException("SimpleFIN took too long to answer. Try again later.");
        }
    }

    internal static SimpleFinAccountSet Parse(string json)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new SimpleFinException("SimpleFIN sent a reply we could not read.");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new SimpleFinException("SimpleFIN sent a reply we could not read.");
        }

        var messages = new List<string>();
        var authProblem = false;

        // Version 1 sends plain strings under "errors"; the newer shape sends coded objects under "errlist".
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in errors.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String))
            {
                var text = e.GetString() ?? string.Empty;
                authProblem |= AuthText.IsMatch(text);
                AddMessage(messages, text);
            }
        }
        if (root.TryGetProperty("errlist", out var errlist) && errlist.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in errlist.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
            {
                var code = Text(e, "code");
                authProblem |= code.StartsWith("con.auth", StringComparison.OrdinalIgnoreCase)
                    || code.StartsWith("gen.auth", StringComparison.OrdinalIgnoreCase);
                AddMessage(messages, Text(e, "msg"));
            }
        }

        var accounts = new List<SimpleFinAccount>();
        if (root.TryGetProperty("accounts", out var accountArray) && accountArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in accountArray.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object))
            {
                var id = Text(a, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var org = string.Empty;
                if (a.TryGetProperty("org", out var o) && o.ValueKind == JsonValueKind.Object)
                {
                    org = Text(o, "name");
                    if (org.Length == 0)
                    {
                        org = Text(o, "domain");
                    }
                }

                var transactions = new List<SimpleFinTransaction>();
                if (a.TryGetProperty("transactions", out var txs) && txs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in txs.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Object))
                    {
                        var tid = Text(t, "id");
                        var amount = Number(t, "amount");
                        if (string.IsNullOrWhiteSpace(tid) || amount == null)
                        {
                            continue;
                        }

                        var posted = Unix(t, "posted");
                        var pending = (t.TryGetProperty("pending", out var p) && p.ValueKind == JsonValueKind.True)
                            || posted == null; // a posted time of 0 means "not posted yet"
                        transactions.Add(new SimpleFinTransaction(
                            tid, posted, amount.Value, Text(t, "description"), Unix(t, "transacted_at"), pending));
                    }
                }

                accounts.Add(new SimpleFinAccount(
                    id,
                    Text(a, "name"),
                    org,
                    Text(a, "currency"),
                    Number(a, "balance"),
                    Number(a, "available-balance"),
                    transactions));
            }
        }

        return new SimpleFinAccountSet(accounts, messages, authProblem);
    }

    /// <summary>
    /// Cleans a bridge error text for display: no links, no control characters, one line, short.
    /// The protocol requires this because the text comes from outside BuoyAI.
    /// </summary>
    internal static string Sanitize(string text)
    {
        var noLinks = Url.Replace(text, "[link]");
        var clean = new string(noLinks.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        clean = Regex.Replace(clean, @"\s+", " ").Trim();
        return clean.Length > 200 ? SafeText.Truncate(clean, 200).TrimEnd() + "..." : clean;
    }

    private static void AddMessage(List<string> messages, string text)
    {
        var clean = Sanitize(text);
        if (clean.Length > 0 && !messages.Contains(clean))
        {
            messages.Add(clean);
        }
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    // The protocol sends money as strings ("-33293.43"); accept plain numbers too.
    private static decimal? Number(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return null;
        }
        return v.ValueKind switch
        {
            JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Number | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var d) => d,
            JsonValueKind.Number when v.TryGetDecimal(out var d) => d,
            _ => null
        };
    }

    private static DateTimeOffset? Unix(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var seconds) || seconds <= 0)
        {
            return null;
        }
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static long ToUnix(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
