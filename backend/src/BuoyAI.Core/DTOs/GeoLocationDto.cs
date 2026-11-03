namespace BuoyAI.Core.DTOs;

/// <summary>
/// Geographic location information from IP address
/// </summary>
public class GeoLocationDto
{
    public string Country { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public string Region { get; set; } = "";
    public string City { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string Timezone { get; set; } = "";
    public string Isp { get; set; } = "";
}
