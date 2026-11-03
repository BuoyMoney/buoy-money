using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.Infrastructure.Data;

public static class DataSeeder
{
  public static async Task SeedAsync(
    BuoyAIDbContext context,
    IHostEnvironment environment,
    ILogger? logger = null,
    MigrationSettings? migrationSettings = null,
    bool seedData = true)
  {
    // Migrations MUST succeed — let failures propagate to the caller (Program.cs), which
    // logs them and fails loudly outside Development. Swallowing here would boot the app
    // against an empty/inconsistent schema (the previous behavior).
    await ApplyMigrationsAsync(context, logger, migrationSettings);
    logger?.LogInformation("Database migrations completed successfully");

    // A self-hosted server starts empty: no admin, reviewer, test or demo accounts. The reviewer
    // accounts share a password published in the source, and any seeded user would also stop the
    // first real sign-up from being recognised as the first account.
    if (!seedData)
    {
      logger?.LogInformation("Seeding skipped (self-host edition)");
      return;
    }

    // Data seeding is best-effort: a transient seeding hiccup shouldn't take the app down.
    try
    {
      await SeedAdminUsers(context, environment);

      // Seed the App Store / Play Store reviewer account (and its family) in every hosted
      // environment: store reviewers log into hosted production, so its data must reflect
      // current features. Self-host returned early above and never gets this account.
      // Fully idempotent — create-once for the account itself, plus an enrichment pass
      // that only ADDS missing pieces (new streak types, core badges, stale-month
      // transaction top-ups) and never modifies existing rows.
      var reviewerPassword = Environment.GetEnvironmentVariable("Reviewer__Password");
      if (string.IsNullOrWhiteSpace(reviewerPassword) && environment.IsDevelopment())
        reviewerPassword = ReviewerAccountSeeder.DevOnlyPassword;
      await ReviewerAccountSeeder.SeedReviewerAccountAsync(context, reviewerPassword);

      // Seed comprehensive test data (RBAC test-user suite + data) in Development AND Staging.
      // Development wipes-and-reseeds each start for a fresh dataset; Staging is idempotent
      // (create-once, no wipe) so it's safe on the long-lived, two-container staging box and
      // survives across deploys. Both give QE the same trial-user/premium-user/family-* logins.
      if (environment.IsDevelopment())
      {
        await TestDataSeeder.SeedTestDataAsync(context, idempotent: false);
      }
      else if (environment.EnvironmentName.Equals("Staging", StringComparison.OrdinalIgnoreCase))
      {
        await TestDataSeeder.SeedTestDataAsync(context, idempotent: true);
      }

      // TestDataSeeder logs and swallows its own failures, but a failed SaveChanges leaves its
      // pending changes tracked, and the next seeder's SaveChanges re-sent them: the demo reseed
      // failed on the test wipe's error, silently, for days. Start each later seeder clean.
      context.ChangeTracker.Clear();

      // Dedicated QE edge-case fixture (pathological data, #615 §0) — Development AND Staging.
      // Idempotent + transactional; runs AFTER TestDataSeeder so the Dev wipe (which preserves it
      // via the exclusion list) doesn't race its creation.
      if (environment.IsDevelopment()
          || environment.EnvironmentName.Equals("Staging", StringComparison.OrdinalIgnoreCase))
      {
        await EdgeCaseAccountSeeder.SeedAsync(context);
      }

      context.ChangeTracker.Clear();

      // Per-clip demo accounts for filming short-form marketing videos
      // Development AND Staging —
      // filming happens on a real device against staging (the Play build can't point
      // anywhere else without a rebuild). Wipes and reseeds ONLY its own demo-* emails
      // each start, so resetting a filming set = restart the API container. Never
      // Production: these are shared-password accounts. No Stripe objects are created
      // (tier is plain User fields), so staging's live Stripe account is untouched.
      if (environment.IsDevelopment()
          || environment.EnvironmentName.Equals("Staging", StringComparison.OrdinalIgnoreCase))
      {
        await DemoAccountSeeder.SeedAsync(context);
      }

      Console.WriteLine($"Database seeding completed for environment: {environment.EnvironmentName}");
    }
    catch (Exception ex)
    {
      Console.WriteLine($"Error seeding data (non-fatal): {ex.Message}");
      Console.WriteLine($"Stack trace: {ex.StackTrace}");
    }
  }

  private static async Task ApplyMigrationsAsync(
    BuoyAIDbContext context, ILogger? logger, MigrationSettings? migrationSettings)
  {
    try
    {
      // InMemory databases (tests) don't support migrations — build the schema from the
      // EF model instead. This path is unchanged by the SQL-migration cutover (#533).
      var providerName = context.Database.ProviderName;
      if (providerName?.Contains("InMemory") == true)
      {
        logger?.LogInformation("Using InMemory database, skipping SQL migrations...");
        await context.Database.EnsureCreatedAsync();
        return;
      }

      // SQL-first migrations (#533): hand-written *.up.sql scripts applied by DbUp, journaled
      // to "schemaversions". Replaces EF Core's MigrateAsync(). See SqlMigrationRunner for the
      // fresh-DB vs existing-EF-DB (baseline pre-seed) handling.
      // GetConnectionString() reads the EF options' ConnectionString, which is null when the
      // provider is configured via an NpgsqlDataSource (e.g. under .NET Aspire). Fall back to
      // the live DbConnection's connection string, which is populated in both cases.
      var connectionString = context.Database.GetConnectionString();
      if (string.IsNullOrEmpty(connectionString))
      {
        connectionString = context.Database.GetDbConnection().ConnectionString;
      }
      if (string.IsNullOrEmpty(connectionString))
      {
        throw new InvalidOperationException("No connection string available for SQL migrations.");
      }

      logger?.LogInformation("Applying SQL migrations (DbUp)...");
      SqlMigrationRunner.Run(connectionString, logger, migrationSettings);
      logger?.LogInformation("SQL migrations applied successfully.");
    }
    catch (Exception ex)
    {
      logger?.LogError(ex, "Error applying migrations");
      throw;
    }
  }

  private static async Task SeedAdminUsers(BuoyAIDbContext context, IHostEnvironment environment)
  {
    // Define admin users for different environments
    var adminUsers = new List<(string username, string email, string environment)>
    {
      ("admin", "local-admin@buoy-ai.com", "Development"),
      ("staging-admin", "staging-admin@buoyai.local", "Staging")
    };

    foreach (var (username, email, envName) in adminUsers)
    {
      // Only seed admin users for the current environment or if it's Development (seed all for local dev)
      if (environment.IsDevelopment() || environment.EnvironmentName.Equals(envName, StringComparison.OrdinalIgnoreCase))
      {
        // Check if this specific admin user already exists
        var existingUser = await context.Users.FirstOrDefaultAsync(u => u.Email == email);
        
        if (existingUser == null)
        {
          // Use a secure password that meets our security requirements:
          // - At least 8 characters
          // - Contains uppercase, lowercase, number, and special character
          // - Not a common weak password
          var securePassword = "BuoyDevLocal2025!";
          
          var adminUser = new User
          {
            Username = username,
            PasswordHash = AuthService.HashPassword(securePassword),
            Email = email,
            FirstName = "BuoyAI",
            LastName = envName == "Staging" ? "Staging Admin" : "Admin",
            Currency = "USD",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
          };

          context.Users.Add(adminUser);
          await context.SaveChangesAsync();

          Console.WriteLine($"Seeded admin user for {envName}:");
          Console.WriteLine($"Username: {username}");
          Console.WriteLine($"Password: {securePassword}");
          Console.WriteLine($"Email: {email}");
        }
        else
        {
          Console.WriteLine($"Admin user {email} already exists for {envName}");
        }
      }
    }
  }
}
