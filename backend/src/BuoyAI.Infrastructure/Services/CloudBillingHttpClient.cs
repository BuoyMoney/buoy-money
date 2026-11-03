using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.DTOs.Billing;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// <see cref="ICloudBillingClient"/> over HTTP. The only code in the app that knows the Cloud
/// billing service exists, and it knows it only as a URL and a shared secret.
/// </summary>
public sealed class CloudBillingHttpClient : ICloudBillingClient
{
    public const string HttpClientName = "cloud-billing";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _factory;
    private readonly CloudBillingOptions _options;

    public CloudBillingHttpClient(IHttpClientFactory factory, IOptions<CloudBillingOptions> options)
    {
        _factory = factory;
        _options = options.Value;
    }

    public async Task<CloudCatalog> GetCatalogAsync(bool founding, CancellationToken ct = default) =>
        (await SendAsync<CloudCatalog>(HttpMethod.Get, "/v1/catalog?founding=" + (founding ? "true" : "false"), null, null, ct))!;

    public async Task<CloudCheckoutResponse> CheckoutAsync(CloudCheckoutRequest request, CancellationToken ct = default) =>
        (await SendAsync<CloudCheckoutResponse>(HttpMethod.Post, "/v1/checkout", request, request.IdempotencyKey, ct))!;

    public async Task<CloudSubscriptionState?> GetSubscriptionAsync(string customerId, string environment, CancellationToken ct = default)
    {
        try
        {
            return await SendAsync<CloudSubscriptionState>(
                HttpMethod.Get,
                "/v1/subscription?customerId=" + Uri.EscapeDataString(customerId)
                    + "&environment=" + Uri.EscapeDataString(environment),
                null, null, ct);
        }
        catch (CloudBillingException ex) when (ex.Code == "no_subscription")
        {
            return null;
        }
    }

    public async Task<CloudPreview> PreviewAsync(CloudChangeRequest request, CancellationToken ct = default) =>
        (await SendAsync<CloudPreview>(HttpMethod.Post, "/v1/subscription/preview", request, null, ct))!;

    public async Task<CloudSubscriptionState> ApplyAsync(CloudChangeRequest request, CancellationToken ct = default) =>
        (await SendAsync<CloudSubscriptionState>(HttpMethod.Post, "/v1/subscription/changes", request, request.IdempotencyKey, ct))!;

    public async Task<CloudUpcomingInvoice?> GetUpcomingAsync(string customerId, string environment, CancellationToken ct = default)
    {
        try
        {
            return await SendAsync<CloudUpcomingInvoice>(
                HttpMethod.Get,
                "/v1/subscription/upcoming?customerId=" + Uri.EscapeDataString(customerId)
                    + "&environment=" + Uri.EscapeDataString(environment),
                null, null, ct);
        }
        catch (CloudBillingException ex) when (ex.Code == "no_subscription")
        {
            return null;
        }
    }

    public async Task<CloudPortalSession> CreatePortalSessionAsync(CloudPortalSessionRequest request, CancellationToken ct = default) =>
        (await SendAsync<CloudPortalSession>(HttpMethod.Post, "/v1/portal-session", request, null, ct))!;

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, string? idempotencyKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl) || string.IsNullOrWhiteSpace(_options.ServiceKey))
        {
            throw new CloudBillingException("not_configured", "Cloud billing is not configured on this server.", 503);
        }

        var http = _factory.CreateClient(HttpClientName);
        http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds));

        using var request = new HttpRequestMessage(method, path.TrimStart('/'));
        request.Headers.Add("X-Service-Key", _options.ServiceKey);
        if (!string.IsNullOrEmpty(idempotencyKey)) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body != null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            throw new CloudBillingException("unreachable", "The billing service could not be reached.", 503);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CloudBillingException("timeout", "The billing service took too long to answer.", 504);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<T>(Json, ct);
            }

            var code = "billing_error";
            var message = "The billing service refused the request.";
            try
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("code", out var c) && c.GetString() is { } cs) code = cs;
                if (doc.RootElement.TryGetProperty("message", out var m) && m.GetString() is { } ms) message = ms;
            }
            catch (JsonException)
            {
                // Not our JSON (a proxy error page): keep the generic answer, never echo the body.
            }

            // A 401 from the service means OUR key is wrong: a configuration problem, not the customer's.
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new CloudBillingException("not_configured", "Cloud billing is not configured on this server.", 503);
            }
            throw new CloudBillingException(code, message, (int)response.StatusCode);
        }
    }
}
