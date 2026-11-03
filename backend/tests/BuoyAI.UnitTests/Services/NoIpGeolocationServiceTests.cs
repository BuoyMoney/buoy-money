using BuoyAI.Infrastructure.Services;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class NoIpGeolocationServiceTests
{
    [Fact]
    public async Task GetLocationAsync_ReturnsNullWithoutLookingAnythingUp()
    {
        var service = new NoIpGeolocationService();

        Assert.Null(await service.GetLocationAsync("203.0.113.9"));
    }
}
