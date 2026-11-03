using BuoyAI.Core.DTOs;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Self-host replacement for the hosted geolocation service. Makes no network calls, so a
/// user's IP address never leaves their own server. Callers already treat a null location as
/// "unknown" and skip the geo-based checks.
/// </summary>
public class NoIpGeolocationService : IIpGeolocationService
{
    public Task<GeoLocationDto?> GetLocationAsync(string ipAddress) => Task.FromResult<GeoLocationDto?>(null);

    public Task<bool> AreLocationsCloseAsync(GeoLocationDto location1, GeoLocationDto location2, int maxDistanceKm = 100) =>
        Task.FromResult(false);

    public double CalculateDistance(double lat1, double lon1, double lat2, double lon2) => 0;
}
