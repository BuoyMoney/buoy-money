using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;
using BuoyAI.Core.DTOs.Billing;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The JSON the app exchanges with the Cloud billing service is pinned by golden files, which the
/// Cloud service's own tests pin from its side. The app never references Cloud code, so these files
/// are the only thing keeping the two in step: if either side renames a field, one of the two
/// test suites fails.
/// </summary>
public class CloudBillingContractTests
{
  private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

  private static string Golden(string file) =>
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", "cloud-billing", file));

  public static IEnumerable<object[]> Files() =>
    new[]
    {
      "checkout-request.json", "checkout-response.json", "change-request.json",
      "subscription-state.json", "preview-response.json", "catalog-response.json",
      "upcoming-invoice.json", "portal-session-request.json", "portal-session.json"
    }.Select(f => new object[] { f });

  private static Type TypeFor(string file) => file switch
  {
    "checkout-request.json" => typeof(CloudCheckoutRequest),
    "checkout-response.json" => typeof(CloudCheckoutResponse),
    "change-request.json" => typeof(CloudChangeRequest),
    "subscription-state.json" => typeof(CloudSubscriptionState),
    "preview-response.json" => typeof(CloudPreview),
    "catalog-response.json" => typeof(CloudCatalog),
    "upcoming-invoice.json" => typeof(CloudUpcomingInvoice),
    "portal-session-request.json" => typeof(CloudPortalSessionRequest),
    "portal-session.json" => typeof(CloudPortalSession),
    _ => throw new ArgumentException(file)
  };

  [Theory]
  [MemberData(nameof(Files))]
  public void TheAppsTypes_ReadAndWriteTheGoldenFile_WithoutLoss(string file)
  {
    var json = Golden(file);
    var value = JsonSerializer.Deserialize(json, TypeFor(file), Web);
    value.Should().NotBeNull();

    var written = JsonSerializer.Serialize(value, TypeFor(file), Web);

    JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(written))
      .Should().BeTrue($"{file} must survive a round trip through the app's types; a field was renamed, dropped or added on one side only");
  }

  [Fact]
  public void TheState_CarriesThePendingChangesTheScreenNeeds()
  {
    var state = JsonSerializer.Deserialize<CloudSubscriptionState>(Golden("subscription-state.json"), Web)!;

    state.Founding.Should().BeTrue();
    state.Interval.Should().Be("yearly");
    state.Products.Single(p => p.Product == "ai").EndsAt.Should().Be(new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc));
    state.Products.Single(p => p.Product == "extra_logins").QuantityAfterPeriodEnd.Should().Be(2);
    state.Pending.Select(p => p.Kind).Should().BeEquivalentTo("remove", "quantity");
  }

  [Fact]
  public void TheUpcomingInvoice_CarriesEachKindOfLineTheBillingScreenShows()
  {
    var invoice = JsonSerializer.Deserialize<CloudUpcomingInvoice>(Golden("upcoming-invoice.json"), Web)!;

    invoice.Founding.Should().BeTrue();
    invoice.TotalCents.Should().Be(3149);
    invoice.Lines.Select(l => l.Kind).Should().BeEquivalentTo("product", "product", "ai_overage", "refresh_overage");
    invoice.Lines.Single(l => l.Product == "extra_logins").Should().BeEquivalentTo(
      new CloudUpcomingLine("product", "extra_logins", "Extra bank logins", 2, 75, 150, false));
  }

  [Fact]
  public void TheRequests_TheAppSends_HaveEveryFieldTheServiceReads()
  {
    var checkout = JsonNode.Parse(JsonSerializer.Serialize(new CloudCheckoutRequest(
      "cus_1", 42, "production", "yearly", true,
      [new CloudProductSelection("bank_sync"), new CloudProductSelection("extra_logins", 3)],
      "https://buoy-ai.com/#/billing?checkout=success", "https://buoy-ai.com/#/billing?checkout=cancel", "idem-1", 100), Web));

    JsonNode.DeepEquals(checkout, JsonNode.Parse(Golden("checkout-request.json"))).Should().BeTrue();

    var change = JsonNode.Parse(JsonSerializer.Serialize(new CloudChangeRequest(
      "cus_1", "production", "set_quantity", "extra_logins", 2,
      new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc), "idem-2"), Web));

    JsonNode.DeepEquals(change, JsonNode.Parse(Golden("change-request.json"))).Should().BeTrue();

    var portal = JsonNode.Parse(JsonSerializer.Serialize(new CloudPortalSessionRequest(
      "cus_1", "production", "https://buoy-ai.com/#/settings/cloud/billing"), Web));

    JsonNode.DeepEquals(portal, JsonNode.Parse(Golden("portal-session-request.json"))).Should().BeTrue();
  }
}
