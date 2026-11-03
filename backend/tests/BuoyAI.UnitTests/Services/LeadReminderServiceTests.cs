using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class LeadReminderServiceTests
{
    private static MarketingLead Lead(string email, int days) => new()
    {
        Email = email,
        OfferedTrialDays = days,
        UnsubscribeToken = Guid.NewGuid().ToString("N"),
        ConfirmationSentAt = DateTime.UtcNow.AddDays(-10),
        CreatedAt = DateTime.UtcNow.AddDays(-10),
        UpdatedAt = DateTime.UtcNow.AddDays(-10),
    };

    [Fact]
    public async Task Sweep_RemindsOnlyLeadsWithAnOfferedTrial()
    {
        var dbName = Guid.NewGuid().ToString();
        var email = new Mock<IEmailService>();
        var services = new ServiceCollection()
            .AddDbContext<BuoyAIDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddSingleton(email.Object)
            .BuildServiceProvider();

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
            db.MarketingLeads.AddRange(Lead("eligible@example.com", 30), Lead("existing@example.com", 0));
            await db.SaveChangesAsync();
        }

        var sut = new LeadReminderService(
            services.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<LeadReminderService>.Instance);

        await sut.SweepAsync(CancellationToken.None);

        email.Verify(e => e.SendExtendedTrialReminderAsync("eligible@example.com", It.IsAny<string>()), Times.Once);
        email.Verify(e => e.SendExtendedTrialReminderAsync("existing@example.com", It.IsAny<string>()), Times.Never);
    }
}
