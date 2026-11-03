using Xunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Repositories;

namespace BuoyAI.UnitTests.Repositories;

public class AIInsightRepositoryTests
{
    private static BuoyAIDbContext GetInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new BuoyAIDbContext(options);
    }

    private static User CreateTestUser(int id)
    {
        return new User
        {
            Id = id,
            Username = $"testuser{id}",
            Email = $"test{id}@example.com",
            PasswordHash = "hash"
        };
    }

    #region GetInsightsForUserAsync Tests

    [Fact]
    public async Task GetInsightsForUserAsync_ReturnsInsightsOrderedByGeneratedAtDesc()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1);
        context.Users.Add(user);
        
        var insight1 = new AIInsight
        {
            Id = 1,
            UserId = 1,
            Context = "dashboard",
            Type = "spending",
            Title = "Insight 1",
            Message = "Message 1",
            GeneratedAt = DateTime.UtcNow.AddDays(-2),
            CreatedAt = DateTime.UtcNow.AddDays(-2)
        };
        
        var insight2 = new AIInsight
        {
            Id = 2,
            UserId = 1,
            Context = "dashboard",
            Type = "budget",
            Title = "Insight 2",
            Message = "Message 2",
            GeneratedAt = DateTime.UtcNow.AddDays(-1),
            CreatedAt = DateTime.UtcNow.AddDays(-1)
        };
        
        context.AIInsights.AddRange(insight1, insight2);
        await context.SaveChangesAsync();

        var repository = new AIInsightRepository(context);

        // Act
        var result = await repository.GetInsightsForUserAsync(1, "dashboard");

        // Assert
        result.Should().HaveCount(2);
        result[0].Id.Should().Be(2); // Most recent first
        result[1].Id.Should().Be(1);
    }

    [Fact]
    public async Task GetInsightsForUserAsync_ExcludesDismissedByDefault()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1);
        context.Users.Add(user);
        
        var insight1 = new AIInsight
        {
            Id = 1,
            UserId = 1,
            Context = "dashboard",
            Type = "spending",
            Title = "Active Insight",
            Message = "Message 1",
            IsDismissed = false,
            GeneratedAt = DateTime.UtcNow
        };
        
        var insight2 = new AIInsight
        {
            Id = 2,
            UserId = 1,
            Context = "dashboard",
            Type = "budget",
            Title = "Dismissed Insight",
            Message = "Message 2",
            IsDismissed = true,
            GeneratedAt = DateTime.UtcNow
        };
        
        context.AIInsights.AddRange(insight1, insight2);
        await context.SaveChangesAsync();

        var repository = new AIInsightRepository(context);

        // Act
        var result = await repository.GetInsightsForUserAsync(1, "dashboard");

        // Assert
        result.Should().HaveCount(1);
        result[0].Id.Should().Be(1);
        result[0].Title.Should().Be("Active Insight");
    }

    [Fact]
    public async Task GetInsightsForUserAsync_IncludesDismissedWhenRequested()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1);
        context.Users.Add(user);
        
        var insight1 = new AIInsight
        {
            Id = 1,
            UserId = 1,
            Context = "dashboard",
            Type = "spending",
            Title = "Active Insight",
            Message = "Message 1",
            IsDismissed = false,
            GeneratedAt = DateTime.UtcNow
        };
        
        var insight2 = new AIInsight
        {
            Id = 2,
            UserId = 1,
            Context = "dashboard",
            Type = "budget",
            Title = "Dismissed Insight",
            Message = "Message 2",
            IsDismissed = true,
            GeneratedAt = DateTime.UtcNow
        };
        
        context.AIInsights.AddRange(insight1, insight2);
        await context.SaveChangesAsync();

        var repository = new AIInsightRepository(context);

        // Act
        var result = await repository.GetInsightsForUserAsync(1, "dashboard", includesDismissed: true);

        // Assert
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetInsightsForUserAsync_ExcludesArchivedByDefault()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1);
        context.Users.Add(user);
        
        var insight1 = new AIInsight
        {
            Id = 1,
            UserId = 1,
            Context = "dashboard",
            Type = "spending",
            Title = "Active Insight",
            Message = "Message 1",
            IsArchived = false,
            GeneratedAt = DateTime.UtcNow
        };
        
        var insight2 = new AIInsight
        {
            Id = 2,
            UserId = 1,
            Context = "dashboard",
            Type = "budget",
            Title = "Archived Insight",
            Message = "Message 2",
            IsArchived = true,
            GeneratedAt = DateTime.UtcNow
        };
        
        context.AIInsights.AddRange(insight1, insight2);
        await context.SaveChangesAsync();

        var repository = new AIInsightRepository(context);

        // Act
        var result = await repository.GetInsightsForUserAsync(1, "dashboard");

        // Assert
        result.Should().HaveCount(1);
        result[0].Id.Should().Be(1);
        result[0].Title.Should().Be("Active Insight");
    }

    [Fact]
    public async Task GetInsightsForUserAsync_RespectsLimit()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1);
        context.Users.Add(user);
        
        for (int i = 1; i <= 15; i++)
        {
            context.AIInsights.Add(new AIInsight
            {
                Id = i,
                UserId = 1,
                Context = "dashboard",
                Type = "spending",
                Title = $"Insight {i}",
                Message = $"Message {i}",
                GeneratedAt = DateTime.UtcNow.AddDays(-i)
            });
        }
        
        await context.SaveChangesAsync();

        var repository = new AIInsightRepository(context);

        // Act
        var result = await repository.GetInsightsForUserAsync(1, "dashboard", limit: 5);

        // Assert
        result.Should().HaveCount(5);
    }

    [Fact]
    public async Task GetInsightsForUserAsync_FiltersByContext()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1);
        context.Users.Add(user);
        
        var insight1 = new AIInsight
        {
            Id = 1,
            UserId = 1,
            Context = "dashboard",
            Type = "spending",
            Title = "Dashboard Insight",
            Message = "Message 1",
            GeneratedAt = DateTime.UtcNow
        };
        
        var insight2 = new AIInsight
        {
            Id = 2,
            UserId = 1,
            Context = "budget",
            Type = "budget",
            Title = "Budget Insight",
            Message = "Message 2",
            GeneratedAt = DateTime.UtcNow
        };
        
        context.AIInsights.AddRange(insight1, insight2);
        await context.SaveChangesAsync();

        var repository = new AIInsightRepository(context);

        // Act
        var result = await repository.GetInsightsForUserAsync(1, "budget");

        // Assert
        result.Should().HaveCount(1);
        result[0].Context.Should().Be("budget");
        result[0].Title.Should().Be("Budget Insight");
    }

    #endregion

    #region GetRecentInsightsForUserAsync Tests

    [Fact]
    public async Task GetRecentInsightsForUserAsync_ReturnsOnlyInsightsSinceDate()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1);
        context.Users.Add(user);
        
        var sinceDate = DateTime.UtcNow.AddDays(-3);
        
        var insight1 = new AIInsight
        {
            Id = 1,
            UserId = 1,
            Context = "dashboard",
            Type = "spending",
            Title = "Old Insight",
            Message = "Message 1",
            GeneratedAt = DateTime.UtcNow.AddDays(-5)
        };
        
        var insight2 = new AIInsight
        {
            Id = 2,
            UserId = 1,
            Context = "dashboard",
            Type = "budget",
            Title = "Recent Insight",
            Message = "Message 2",
            GeneratedAt = DateTime.UtcNow.AddDays(-1)
        };
        
        context.AIInsights.AddRange(insight1, insight2);
        await context.SaveChangesAsync();

        var repository = new AIInsightRepository(context);

        // Act
        var result = await repository.GetRecentInsightsForUserAsync(1, "dashboard", sinceDate);

        // Assert
        result.Should().HaveCount(1);
        result[0].Id.Should().Be(2);
        result[0].Title.Should().Be("Recent Insight");
    }

    #endregion

    #region GetInsightByIdAsync Tests

    [Fact]
    public async Task GetInsightByIdAsync_ReturnsInsight_WhenExists()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1);
        context.Users.Add(user);
        
        var insight = new AIInsight
        {
            Id = 1,
            UserId = 1,
            Context = "dashboard",
            Type = "spending",
            Title = "Test Insight",
            Message = "Test Message",
            GeneratedAt = DateTime.UtcNow
        };
        
        context.AIInsights.Add(insight);
        await context.SaveChangesAsync();

        var repository = new AIInsightRepository(context);

        // Act
        var result = await repository.GetInsightByIdAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(1);
        result.Title.Should().Be("Test Insight");
    }

    [Fact]
    public async Task GetInsightByIdAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new AIInsightRepository(context);

        // Act
        var result = await repository.GetInsightByIdAsync(999);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region CreateInsightAsync Tests

    [Fact]
    public async Task CreateInsightAsync_AddsInsight_AndSetsTimestamps()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var repository = new AIInsightRepository(context);
        
        var insight = new AIInsight
        {
            UserId = 1,
            Context = "dashboard",
            Type = "spending",
            Title = "New Insight",
            Message = "New Message",
            Priority = "high",
            Confidence = 0.95,
            GeneratedAt = DateTime.UtcNow
        };

        // Act
        var result = await repository.CreateInsightAsync(insight);

        // Assert
        result.Id.Should().BeGreaterThan(0);
        result.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        result.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        
        var savedInsight = await context.AIInsights.FindAsync(result.Id);
        savedInsight.Should().NotBeNull();
        savedInsight!.Title.Should().Be("New Insight");
    }

    #endregion

    #region UpdateInsightAsync Tests

    [Fact]
    public async Task UpdateInsightAsync_UpdatesInsight_AndSetsUpdatedAt()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1);
        context.Users.Add(user);
        
        var insight = new AIInsight
        {
            Id = 1,
            UserId = 1,
            Context = "dashboard",
            Type = "spending",
            Title = "Original Title",
            Message = "Original Message",
            GeneratedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow.AddHours(-1)
        };
        
        context.AIInsights.Add(insight);
        await context.SaveChangesAsync();

        var repository = new AIInsightRepository(context);
        
        // Wait a moment to ensure UpdatedAt changes
        await Task.Delay(100);
        
        // Detach to avoid tracking conflicts
        context.Entry(insight).State = EntityState.Detached;
        
        insight.Title = "Updated Title";

        // Act
        var result = await repository.UpdateInsightAsync(insight);

        // Assert
        result.Title.Should().Be("Updated Title");
        result.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    #endregion

    #region DismissInsightAsync Tests

    [Fact]
    public async Task DismissInsightAsync_DismissesInsight_WhenFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1);
        context.Users.Add(user);
        
        var insight = new AIInsight
        {
            Id = 1,
            UserId = 1,
            Context = "dashboard",
            Type = "spending",
            Title = "Test Insight",
            Message = "Test Message",
            IsDismissed = false,
            GeneratedAt = DateTime.UtcNow
        };
        
        context.AIInsights.Add(insight);
        await context.SaveChangesAsync();

        var repository = new AIInsightRepository(context);

        // Act
        var result = await repository.DismissInsightAsync(1, "1");

        // Assert
        result.Should().BeTrue();
        
        var dismissedInsight = await context.AIInsights.FindAsync(1);
        dismissedInsight.Should().NotBeNull();
        dismissedInsight!.IsDismissed.Should().BeTrue();
        dismissedInsight.DismissedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DismissInsightAsync_ReturnsFalse_WhenNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new AIInsightRepository(context);

        // Act
        var result = await repository.DismissInsightAsync(1, "999");

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region ArchiveInsightsAsync Tests

    [Fact]
    public async Task ArchiveInsightsAsync_ArchivesActiveInsights()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1);
        context.Users.Add(user);
        
        var insight1 = new AIInsight
        {
            Id = 1,
            UserId = 1,
            Context = "dashboard",
            Type = "spending",
            Title = "Insight 1",
            Message = "Message 1",
            IsArchived = false,
            IsDismissed = false,
            GeneratedAt = DateTime.UtcNow
        };
        
        var insight2 = new AIInsight
        {
            Id = 2,
            UserId = 1,
            Context = "dashboard",
            Type = "budget",
            Title = "Insight 2",
            Message = "Message 2",
            IsArchived = false,
            IsDismissed = false,
            GeneratedAt = DateTime.UtcNow
        };
        
        context.AIInsights.AddRange(insight1, insight2);
        await context.SaveChangesAsync();

        var repository = new AIInsightRepository(context);

        // Act
        var result = await repository.ArchiveInsightsAsync(1, "dashboard");

        // Assert
        result.Should().BeTrue();
        
        var archivedInsights = await context.AIInsights.Where(i => i.UserId == 1).ToListAsync();
        archivedInsights.Should().AllSatisfy(i => i.IsArchived.Should().BeTrue());
    }

    [Fact]
    public async Task ArchiveInsightsAsync_ReturnsFalse_WhenNoActiveInsights()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1);
        context.Users.Add(user);
        
        var insight = new AIInsight
        {
            Id = 1,
            UserId = 1,
            Context = "dashboard",
            Type = "spending",
            Title = "Already Archived",
            Message = "Message",
            IsArchived = true,
            GeneratedAt = DateTime.UtcNow
        };
        
        context.AIInsights.Add(insight);
        await context.SaveChangesAsync();

        var repository = new AIInsightRepository(context);

        // Act
        var result = await repository.ArchiveInsightsAsync(1, "dashboard");

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region DeleteOldInsightsAsync Tests

    [Fact]
    public async Task DeleteOldInsightsAsync_DeletesOldInsights()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1);
        context.Users.Add(user);
        
        var cutoffDate = DateTime.UtcNow.AddDays(-7);
        
        var oldInsight = new AIInsight
        {
            Id = 1,
            UserId = 1,
            Context = "dashboard",
            Type = "spending",
            Title = "Old Insight",
            Message = "Message",
            GeneratedAt = DateTime.UtcNow.AddDays(-10)
        };
        
        var recentInsight = new AIInsight
        {
            Id = 2,
            UserId = 1,
            Context = "dashboard",
            Type = "budget",
            Title = "Recent Insight",
            Message = "Message",
            GeneratedAt = DateTime.UtcNow.AddDays(-3)
        };
        
        context.AIInsights.AddRange(oldInsight, recentInsight);
        await context.SaveChangesAsync();

        var repository = new AIInsightRepository(context);

        // Act
        var result = await repository.DeleteOldInsightsAsync(1, "dashboard", cutoffDate);

        // Assert
        result.Should().BeTrue();
        
        var remainingInsights = await context.AIInsights.ToListAsync();
        remainingInsights.Should().HaveCount(1);
        remainingInsights[0].Id.Should().Be(2);
    }

    [Fact]
    public async Task DeleteOldInsightsAsync_ReturnsFalse_WhenNoOldInsights()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1);
        context.Users.Add(user);
        
        var cutoffDate = DateTime.UtcNow.AddDays(-7);
        
        var recentInsight = new AIInsight
        {
            Id = 1,
            UserId = 1,
            Context = "dashboard",
            Type = "spending",
            Title = "Recent Insight",
            Message = "Message",
            GeneratedAt = DateTime.UtcNow.AddDays(-3)
        };
        
        context.AIInsights.Add(recentInsight);
        await context.SaveChangesAsync();

        var repository = new AIInsightRepository(context);

        // Act
        var result = await repository.DeleteOldInsightsAsync(1, "dashboard", cutoffDate);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region GetInsightCountForUserAsync Tests

    [Fact]
    public async Task GetInsightCountForUserAsync_ReturnsCorrectCount()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1);
        context.Users.Add(user);
        
        var sinceDate = DateTime.UtcNow.AddDays(-3);
        
        var oldInsight = new AIInsight
        {
            Id = 1,
            UserId = 1,
            Context = "dashboard",
            Type = "spending",
            Title = "Old Insight",
            Message = "Message",
            GeneratedAt = DateTime.UtcNow.AddDays(-5)
        };
        
        var recentInsight1 = new AIInsight
        {
            Id = 2,
            UserId = 1,
            Context = "dashboard",
            Type = "budget",
            Title = "Recent Insight 1",
            Message = "Message",
            GeneratedAt = DateTime.UtcNow.AddDays(-2)
        };
        
        var recentInsight2 = new AIInsight
        {
            Id = 3,
            UserId = 1,
            Context = "dashboard",
            Type = "savings",
            Title = "Recent Insight 2",
            Message = "Message",
            GeneratedAt = DateTime.UtcNow.AddDays(-1)
        };
        
        context.AIInsights.AddRange(oldInsight, recentInsight1, recentInsight2);
        await context.SaveChangesAsync();

        var repository = new AIInsightRepository(context);

        // Act
        var result = await repository.GetInsightCountForUserAsync(1, "dashboard", sinceDate);

        // Assert
        result.Should().Be(2);
    }

    [Fact]
    public async Task GetInsightCountForUserAsync_ReturnsZero_WhenNoInsights()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new AIInsightRepository(context);

        // Act
        var result = await repository.GetInsightCountForUserAsync(1, "dashboard", DateTime.UtcNow.AddDays(-7));

        // Assert
        result.Should().Be(0);
    }

    #endregion
}
