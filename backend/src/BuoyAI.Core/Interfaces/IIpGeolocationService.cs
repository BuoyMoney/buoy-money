using BuoyAI.Core.DTOs;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Service for IP address geolocation lookups
/// </summary>
public interface IIpGeolocationService
{
    /// <summary>
    /// Get geographic location for an IP address
    /// </summary>
    Task<GeoLocationDto?> GetLocationAsync(string ipAddress);
    
    /// <summary>
    /// Check if two locations are within a specified distance
    /// </summary>
    Task<bool> AreLocationsCloseAsync(GeoLocationDto location1, GeoLocationDto location2, int maxDistanceKm = 100);
    
    /// <summary>
    /// Calculate distance in kilometers between two geographic coordinates
    /// </summary>
    double CalculateDistance(double lat1, double lon1, double lat2, double lon2);
}
