using System.Net;
using BuoyAI.Infrastructure.Services;
using MaxMind.GeoIP2.Responses;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class LocalIpGeolocationServiceTests
{
  private sealed class FakeReader(Func<IPAddress, CityResponse?> find) : IGeoCityReader
  {
    public int Calls;
    public CityResponse? Find(IPAddress address) { Calls++; return find(address); }
    public void Dispose() { }
  }

  private static CityResponse Topeka() => System.Text.Json.JsonSerializer.Deserialize<CityResponse>("""
    {
      "city": { "names": { "en": "Topeka" } },
      "country": { "iso_code": "US", "names": { "en": "United States" } },
      "location": { "latitude": 39.05, "longitude": -95.68, "time_zone": "America/Chicago" },
      "subdivisions": [ { "iso_code": "KS", "names": { "en": "Kansas" } } ]
    }
    """)!;

  [Fact]
  public async Task PublicIp_MapsCityRegionCountry()
  {
    var svc = new LocalIpGeolocationService(new FakeReader(_ => Topeka()), NullLogger<LocalIpGeolocationService>.Instance);

    var loc = await svc.GetLocationAsync("8.8.8.8");

    Assert.NotNull(loc);
    Assert.Equal("Topeka", loc!.City);
    Assert.Equal("Kansas", loc.Region);
    Assert.Equal("United States", loc.Country);
    Assert.Equal("US", loc.CountryCode);
    Assert.Equal(39.05, loc.Latitude);
    Assert.Equal(-95.68, loc.Longitude);
    Assert.Equal("America/Chicago", loc.Timezone);
  }

  [Theory]
  [InlineData("")]
  [InlineData("not-an-ip")]
  [InlineData("127.0.0.1")]
  [InlineData("::1")]
  [InlineData("10.1.2.3")]
  [InlineData("172.20.0.5")]
  [InlineData("192.168.1.9")]
  [InlineData("169.254.1.1")]
  [InlineData("100.64.0.1")]
  [InlineData("fe80::1")]
  [InlineData("fd00::1")]
  [InlineData("::ffff:10.0.0.1")]
  public async Task PrivateOrInvalid_ReturnsNullWithoutLookup(string ip)
  {
    var reader = new FakeReader(_ => Topeka());
    var svc = new LocalIpGeolocationService(reader, NullLogger<LocalIpGeolocationService>.Instance);

    Assert.Null(await svc.GetLocationAsync(ip));
    Assert.Equal(0, reader.Calls);
  }

  [Fact]
  public async Task IPv4MappedPublicAddress_IsLookedUpAsIPv4()
  {
    IPAddress? seen = null;
    var svc = new LocalIpGeolocationService(new FakeReader(a => { seen = a; return Topeka(); }), NullLogger<LocalIpGeolocationService>.Instance);

    await svc.GetLocationAsync("::ffff:8.8.8.8");

    Assert.Equal(IPAddress.Parse("8.8.8.8"), seen);
  }

  [Fact]
  public async Task NoRecordOrEmptyRecord_ReturnsNull()
  {
    var none = new LocalIpGeolocationService(new FakeReader(_ => null), NullLogger<LocalIpGeolocationService>.Instance);
    var empty = new LocalIpGeolocationService(new FakeReader(_ => new CityResponse()), NullLogger<LocalIpGeolocationService>.Instance);

    Assert.Null(await none.GetLocationAsync("8.8.8.8"));
    Assert.Null(await empty.GetLocationAsync("8.8.8.8"));
  }

  [Fact]
  public async Task ReaderThrows_ReturnsNull()
  {
    var svc = new LocalIpGeolocationService(new FakeReader(_ => throw new InvalidOperationException("boom")), NullLogger<LocalIpGeolocationService>.Instance);

    Assert.Null(await svc.GetLocationAsync("8.8.8.8"));
  }

  [Fact]
  public async Task MissingDatabaseFile_FallsBackToUnknown()
  {
    var config = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { ["IpGeolocation:DatabasePath"] = "/definitely/not/here.mmdb" })
      .Build();

    var svc = new LocalIpGeolocationService(config, NullLogger<LocalIpGeolocationService>.Instance);

    Assert.False(svc.IsAvailable);
    Assert.Null(await svc.GetLocationAsync("8.8.8.8"));
  }

  [Fact]
  public async Task CorruptDatabaseFile_FallsBackToUnknown()
  {
    var path = Path.GetTempFileName();
    try
    {
      await File.WriteAllTextAsync(path, "this is not an mmdb file");
      var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["IpGeolocation:DatabasePath"] = path })
        .Build();

      var svc = new LocalIpGeolocationService(config, NullLogger<LocalIpGeolocationService>.Instance);

      Assert.False(svc.IsAvailable);
      Assert.Null(await svc.GetLocationAsync("8.8.8.8"));
    }
    finally { File.Delete(path); }
  }

  // Opt-in: set BUOYAI_GEO_DB to a downloaded dbip-city-lite.mmdb to exercise the real reader.
  [Fact]
  public async Task RealDatabase_ResolvesPublicIp_WhenProvided()
  {
    var path = Environment.GetEnvironmentVariable("BUOYAI_GEO_DB");
    if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
    var config = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { ["IpGeolocation:DatabasePath"] = path })
      .Build();

    using var svc = new LocalIpGeolocationService(config, NullLogger<LocalIpGeolocationService>.Instance);
    var loc = await svc.GetLocationAsync("8.8.8.8");

    Assert.True(svc.IsAvailable);
    Assert.NotNull(loc);
    Assert.Equal("US", loc!.CountryCode);
  }

  [Fact]
  public async Task Distance_TopekaToKansasCity_IsAbout100Km()
  {
    var svc = new LocalIpGeolocationService((IGeoCityReader?)null, NullLogger<LocalIpGeolocationService>.Instance);

    var km = svc.CalculateDistance(39.05, -95.68, 39.10, -94.58);

    Assert.InRange(km, 90, 100);
  }
}
