using System.Net;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Interfaces;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Responses;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>Reads one city record. A seam so the mapping can be tested without an mmdb file.</summary>
public interface IGeoCityReader : IDisposable
{
    /// <summary>Returns the record for the address, or null when the database has none.</summary>
    CityResponse? Find(IPAddress address);
}

/// <summary>Memory-mapped reader over a MaxMind-format database (DB-IP City Lite). Thread-safe.</summary>
internal sealed class MmdbCityReader : IGeoCityReader
{
    private readonly DatabaseReader _reader;

    public MmdbCityReader(string path) => _reader = new DatabaseReader(path, MaxMind.Db.FileAccessMode.MemoryMapped);

    public CityResponse? Find(IPAddress address) =>
        _reader.TryCity(address, out var response) ? response : null;

    public void Dispose() => _reader.Dispose();
}

/// <summary>
/// Offline IP geolocation from the DB-IP "IP to City Lite" database (CC BY 4.0, db-ip.com), baked
/// into the image at build time. No IP address ever leaves the server. When the database file is
/// missing or unreadable every lookup returns null ("unknown"), which callers already handle.
/// </summary>
public sealed class LocalIpGeolocationService : IIpGeolocationService, IDisposable
{
    public const string DefaultDatabasePath = "/app/geo/dbip-city-lite.mmdb";

    private readonly IGeoCityReader? _reader;
    private readonly ILogger<LocalIpGeolocationService> _logger;

    public LocalIpGeolocationService(IConfiguration configuration, ILogger<LocalIpGeolocationService> logger)
        : this(OpenReader(configuration["IpGeolocation:DatabasePath"] ?? DefaultDatabasePath, logger), logger)
    {
    }

    public LocalIpGeolocationService(IGeoCityReader? reader, ILogger<LocalIpGeolocationService> logger)
    {
        _reader = reader;
        _logger = logger;
    }

    public bool IsAvailable => _reader != null;

    private static IGeoCityReader? OpenReader(string path, ILogger logger)
    {
        try
        {
            if (!File.Exists(path))
            {
                logger.LogWarning("IP geolocation database not found at {Path}; sign-in locations will be unknown", path);
                return null;
            }
            return new MmdbCityReader(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open IP geolocation database at {Path}; sign-in locations will be unknown", path);
            return null;
        }
    }

    public Task<GeoLocationDto?> GetLocationAsync(string ipAddress)
    {
        try
        {
            if (_reader == null || string.IsNullOrWhiteSpace(ipAddress) || !IPAddress.TryParse(ipAddress.Trim(), out var ip))
                return Task.FromResult<GeoLocationDto?>(null);

            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IsNonPublic(ip)) return Task.FromResult<GeoLocationDto?>(null);

            var city = _reader.Find(ip);
            return Task.FromResult(city == null ? null : Map(city));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "IP geolocation lookup failed");
            return Task.FromResult<GeoLocationDto?>(null);
        }
    }

    internal static GeoLocationDto? Map(CityResponse city)
    {
        var country = city.Country?.Name ?? "";
        var cityName = city.City?.Name ?? "";
        if (country == "" && cityName == "") return null;

        return new GeoLocationDto
        {
            Country = country,
            CountryCode = city.Country?.IsoCode ?? "",
            Region = city.MostSpecificSubdivision?.Name ?? "",
            City = cityName,
            Latitude = city.Location?.Latitude ?? 0,
            Longitude = city.Location?.Longitude ?? 0,
            Timezone = city.Location?.TimeZone ?? "",
            Isp = ""
        };
    }

    internal static bool IsNonPublic(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast
                || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7 unique local
        }
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // CGNAT
    }

    public Task<bool> AreLocationsCloseAsync(GeoLocationDto location1, GeoLocationDto location2, int maxDistanceKm = 100) =>
        Task.FromResult(CalculateDistance(location1.Latitude, location1.Longitude, location2.Latitude, location2.Longitude) <= maxDistanceKm);

    public double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusKm = 6371;
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;

    public void Dispose() => _reader?.Dispose();
}

/// <summary>Resolves the geolocation service at startup so a missing database warns in the first log lines.</summary>
internal sealed class GeoDatabaseStartupCheck(LocalIpGeolocationService geo) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
