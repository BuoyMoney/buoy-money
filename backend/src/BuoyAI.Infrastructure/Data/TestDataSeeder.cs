using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.Infrastructure.Data;

/// <summary>
/// Seeds comprehensive test data for development and testing environments.
/// This data is recreated each time Aspire starts with a fresh database.
/// 
/// Test Users (all use password: BuoyDevLocal2025!):
/// - trial-user@buoy-ai.com       : Basic trial user with accounts and transactions
/// - premium-user@buoy-ai.com     : Premium user with MFA enabled
/// - family-owner@buoy-ai.com     : Family owner (Premium) - created the test family
/// - family-member@buoy-ai.com    : Family member (BudgetAdmin role)
/// - family-readonly@buoy-ai.com  : Family member (ReadOnly role)
/// 
/// Plaid Sandbox Login (for testing bank connections):
/// - Username: user_good
/// - Password: pass_good
/// - Any PIN requested: credential_good
/// - MFA code if requested: 1234
/// See: https://plaid.com/docs/sandbox/test-credentials/
/// </summary>
public static class TestDataSeeder
{
    private const string TestPassword = "BuoyDevLocal2025!";
    private static readonly DateTime Now = DateTime.UtcNow;

    /// <param name="idempotent">
    /// When true (Staging), the seed is create-once: it skips entirely if the fixtures already
    /// exist and never wipes. When false (Development), it wipes and reseeds each start for a
    /// fresh dataset.
    /// </param>
    public static async Task SeedTestDataAsync(BuoyAIDbContext context, bool idempotent = false)
    {
        Console.WriteLine("=== Starting Test Data Seeding ===");

        try
        {
            // Seed in order of dependencies
            var users = await SeedTestUsers(context, idempotent);
            if (users.Count == 0)
            {
                Console.WriteLine("Test users already exist, skipping test data seeding");
                // Staging fixtures predate the plan rows, so backfill them for the existing
                // Premium test users (idempotent: skips any user that already has an active row).
                var existing = await context.Users
                    .Where(u => u.Email == "premium-user@buoy-ai.com" || u.Email == "family-owner@buoy-ai.com")
                    .ToDictionaryAsync(u => u.Email);
                await SeedPremiumPlanRows(context, existing);
                return;
            }

            await SeedPremiumPlanRows(context, users);
            var family = await SeedTestFamily(context, users);
            var accounts = await SeedTestAccounts(context, users, family);
            await SeedTestTransactions(context, accounts);
            await SeedSubscriptionDetectionTestPatterns(context, accounts); // Diverse patterns for testing detection
            await SeedTestBudgets(context, users, family);
            await SeedTestSubscriptions(context, users);
            await SeedTestSavingsGoals(context, users, family);
            await SeedTestNotifications(context, users);

            Console.WriteLine("=== Test Data Seeding Complete ===");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error seeding test data: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }

    private static async Task<Dictionary<string, User>> SeedTestUsers(BuoyAIDbContext context, bool idempotent)
    {
        var users = new Dictionary<string, User>();

        if (idempotent)
        {
            // Staging: create the fixtures once, never wipe. If they already exist, skip the whole
            // test-data seed (returning an empty dict makes the caller no-op). This also makes the
            // two-container staging boot safe — there is no destructive delete to race on, and a
            // concurrent duplicate insert simply loses on the unique Email index. All 5 users are
            // created in a single SaveChanges below, so "trial-user exists" ⟺ the full set exists.
            var alreadySeeded = await context.Users.AnyAsync(u => u.Email == "trial-user@buoy-ai.com");
            if (alreadySeeded)
            {
                Console.WriteLine("Test users already present (idempotent mode) — skipping test data seeding");
                return users;
            }
        }
        else
        {
            // Development: delete existing test users and their related data for a fresh reseed
            // each start. The reviewer accounts (seeded by ReviewerAccountSeeder before this runs)
            // are preserved — they are version-controlled fixtures, not disposable test users.
            var existingTestUsers = await context.Users
                .Where(u => u.Email.EndsWith("@buoy-ai.com")
                    && u.Email != "local-admin@buoy-ai.com"
                    && u.Email != "staging-admin@buoyai.local"
                    && !ReviewerAccountSeeder.AllEmails.Contains(u.Email)
                    && u.Email != "edge-owner@buoy-ai.com"
                    && u.Email != "edge-case@buoy-ai.com")
                .ToListAsync();

            if (existingTestUsers.Any())
            {
                Console.WriteLine($"Removing {existingTestUsers.Count} existing test users for fresh seeding...");
                // Families first: FK_Families_Users_CreatedByUserId has no delete rule, so deleting
                // the family-owner user while "Test Family" still points at it was refused, and the
                // whole dev wipe (and, through the shared context, the demo reseed after it) failed
                // on every start from 2026-09-24. Members, invitations and family categories cascade.
                var testUserIds = existingTestUsers.Select(u => u.Id).ToList();
                var ownedFamilies = await context.Families
                    .Where(f => testUserIds.Contains(f.CreatedByUserId))
                    .ToListAsync();
                // Two saves: a family and its creator point at each other (Users.FamilyId and
                // Families.CreatedByUserId), and EF refuses to order both deletes in one save.
                context.Families.RemoveRange(ownedFamilies);
                await context.SaveChangesAsync();
                context.Users.RemoveRange(existingTestUsers);
                await context.SaveChangesAsync();
            }
        }

        Console.WriteLine("Seeding test users...");

        // 1. Trial User - Basic user with trial active
        var trialUser = new User
        {
            Username = "trial-user",
            PasswordHash = AuthService.HashPassword(TestPassword),
            Email = "trial-user@buoy-ai.com",
            FirstName = "Trial",
            LastName = "User",
            Currency = "USD",
            CreatedAt = Now.AddDays(-10),
            UpdatedAt = Now,
            IsTrialActive = true,
            TrialStartDate = Now.AddDays(-10),
            TrialEndDate = Now.AddDays(20), // mid-trial seed
            HasUsedTrial = true
        };
        context.Users.Add(trialUser);
        users["trial"] = trialUser;

        // 2. Premium User - Has active subscription (no MFA for easy testing)
        var premiumUser = new User
        {
            Username = "premium-user",
            PasswordHash = AuthService.HashPassword(TestPassword),
            Email = "premium-user@buoy-ai.com",
            FirstName = "Premium",
            LastName = "User",
            Currency = "USD",
            CreatedAt = Now.AddMonths(-3),
            UpdatedAt = Now,
            SubscriptionTier = SubscriptionTier.Premium,
            SubscriptionStartDate = Now.AddMonths(-2),
            SubscriptionEndDate = Now.AddMonths(10),
            HasMfaEnabled = false,  // Disabled for easy local testing
            HasUsedTrial = true,
            IsTrialActive = false
        };
        context.Users.Add(premiumUser);
        users["premium"] = premiumUser;

        // 3. Family Owner - Created the family, has Premium
        var familyOwner = new User
        {
            Username = "family-owner",
            PasswordHash = AuthService.HashPassword(TestPassword),
            Email = "family-owner@buoy-ai.com",
            FirstName = "Family",
            LastName = "Owner",
            Currency = "USD",
            CreatedAt = Now.AddMonths(-6),
            UpdatedAt = Now,
            SubscriptionTier = SubscriptionTier.Premium,
            SubscriptionStartDate = Now.AddMonths(-5),
            SubscriptionEndDate = Now.AddMonths(7),
            HasUsedTrial = true,
            IsTrialActive = false
        };
        context.Users.Add(familyOwner);
        users["familyOwner"] = familyOwner;

        // 4. Family Member - BudgetAdmin role
        var familyMember = new User
        {
            Username = "family-member",
            PasswordHash = AuthService.HashPassword(TestPassword),
            Email = "family-member@buoy-ai.com",
            FirstName = "Family",
            LastName = "Member",
            Currency = "USD",
            CreatedAt = Now.AddMonths(-4),
            UpdatedAt = Now,
            // Gets PremiumInherited from family
            SubscriptionTier = SubscriptionTier.PremiumInherited
        };
        context.Users.Add(familyMember);
        users["familyMember"] = familyMember;

        // 5. Family ReadOnly - ReadOnly role (e.g., child)
        var familyReadOnly = new User
        {
            Username = "family-readonly",
            PasswordHash = AuthService.HashPassword(TestPassword),
            Email = "family-readonly@buoy-ai.com",
            FirstName = "Family",
            LastName = "Child",
            Currency = "USD",
            CreatedAt = Now.AddMonths(-3),
            UpdatedAt = Now,
            SubscriptionTier = SubscriptionTier.PremiumInherited
        };
        context.Users.Add(familyReadOnly);
        users["familyReadOnly"] = familyReadOnly;

        await context.SaveChangesAsync();
        Console.WriteLine($"Seeded {users.Count} test users");

        return users;
    }

    /// <summary>
    /// Access checks resolve the tier from an active UserSubscription row (or an active trial),
    /// never from User.SubscriptionTier alone, so a seeded "Premium" user with no row was Premium
    /// on the settings page and Standard to every gate (AI chat answered 403 on staging). One
    /// active Premium row per seeded Premium user, with no Stripe ids, makes it real without
    /// touching the shared live Stripe account. Same approach as DemoAccountSeeder.
    /// Family members (PremiumInherited) inherit through the family and need no row.
    /// </summary>
    private static async Task SeedPremiumPlanRows(BuoyAIDbContext context, Dictionary<string, User> users)
    {
        var premium = users.Values.Where(u => u.SubscriptionTier == SubscriptionTier.Premium).ToList();
        foreach (var user in premium)
        {
            var hasActive = await context.UserSubscriptions
                .AnyAsync(s => s.UserId == user.Id && s.Status == UserSubscriptionStatus.Active);
            if (hasActive) continue;
            context.UserSubscriptions.Add(new UserSubscription
            {
                UserId = user.Id,
                Tier = SubscriptionTier.Premium,
                BillingCycle = SubscriptionBillingCycle.Monthly,
                Amount = 0m,
                StartDate = Now.AddMonths(-6),
                NextBillingDate = Now.AddDays(30),
                Status = UserSubscriptionStatus.Active,
                CreatedAt = Now.AddMonths(-6),
                UpdatedAt = Now
            });
        }
        await context.SaveChangesAsync();
        Console.WriteLine($"  Seeded {premium.Count} test Premium plan rows (no Stripe objects)");
    }

    private static async Task<Family?> SeedTestFamily(BuoyAIDbContext context, Dictionary<string, User> users)
    {
        if (!users.ContainsKey("familyOwner"))
            return null;

        Console.WriteLine("Seeding test family...");

        var family = new Family
        {
            Name = "Test Family",
            CreatedByUserId = users["familyOwner"].Id,
            CreatedAt = Now.AddMonths(-5),
            UpdatedAt = Now
        };
        context.Families.Add(family);
        await context.SaveChangesAsync();

        // Update users with FamilyId
        users["familyOwner"].FamilyId = family.Id;
        users["familyMember"].FamilyId = family.Id;
        users["familyReadOnly"].FamilyId = family.Id;

        // Add family members with roles
        var members = new List<FamilyMember>
        {
            new FamilyMember
            {
                FamilyId = family.Id,
                UserId = users["familyOwner"].Id,
                Role = FamilyMemberRole.Owner,
                JoinedAt = Now.AddMonths(-5),
                IsActive = true
            },
            new FamilyMember
            {
                FamilyId = family.Id,
                UserId = users["familyMember"].Id,
                Role = FamilyMemberRole.BudgetAdmin,
                JoinedAt = Now.AddMonths(-4),
                IsActive = true
            },
            new FamilyMember
            {
                FamilyId = family.Id,
                UserId = users["familyReadOnly"].Id,
                Role = FamilyMemberRole.ReadOnly,
                JoinedAt = Now.AddMonths(-3),
                IsActive = true
            }
        };

        context.FamilyMembers.AddRange(members);
        await context.SaveChangesAsync();

        Console.WriteLine($"Seeded family '{family.Name}' with {members.Count} members");
        return family;
    }

    private static async Task<Dictionary<string, Account>> SeedTestAccounts(
        BuoyAIDbContext context, 
        Dictionary<string, User> users,
        Family? family)
    {
        var accounts = new Dictionary<string, Account>();

        Console.WriteLine("Seeding test accounts...");

        // Accounts for trial user (personal)
        var trialChecking = new Account
        {
            UserId = users["trial"].Id,
            PlaidAccountId = "manual-trial-checking",
            Name = "Primary Checking",
            OfficialName = "Personal Checking Account",
            Nickname = "My Checking",
            Mask = "1234",
            Type = AccountType.Checking,
            Subtype = "checking",
            CurrentBalance = 5250.00m,
            AvailableBalance = 5200.00m,
            Currency = "USD",
            IsActive = true,
            CreatedAt = Now.AddDays(-10),
            UpdatedAt = Now
        };
        context.Accounts.Add(trialChecking);
        accounts["trialChecking"] = trialChecking;

        var trialSavings = new Account
        {
            UserId = users["trial"].Id,
            PlaidAccountId = "manual-trial-savings",
            Name = "Savings Account",
            OfficialName = "High-Yield Savings",
            Mask = "5678",
            Type = AccountType.Savings,
            Subtype = "savings",
            CurrentBalance = 12500.00m,
            AvailableBalance = 12500.00m,
            Currency = "USD",
            IsActive = true,
            CreatedAt = Now.AddDays(-10),
            UpdatedAt = Now
        };
        context.Accounts.Add(trialSavings);
        accounts["trialSavings"] = trialSavings;

        var trialCredit = new Account
        {
            UserId = users["trial"].Id,
            PlaidAccountId = "manual-trial-credit",
            Name = "Chase Sapphire",
            OfficialName = "Chase Sapphire Preferred",
            Mask = "9012",
            Type = AccountType.CreditCard,
            Subtype = "credit card",
            CurrentBalance = -1523.45m,
            AvailableBalance = 8476.55m,
            Currency = "USD",
            IsActive = true,
            CreatedAt = Now.AddDays(-10),
            UpdatedAt = Now
        };
        context.Accounts.Add(trialCredit);
        accounts["trialCredit"] = trialCredit;

        // Accounts for premium user
        var premiumChecking = new Account
        {
            UserId = users["premium"].Id,
            PlaidAccountId = "manual-premium-checking",
            Name = "Premium Checking",
            OfficialName = "Chase Total Checking",
            Nickname = "Main Account",
            Mask = "4321",
            Type = AccountType.Checking,
            Subtype = "checking",
            CurrentBalance = 12500.00m,
            AvailableBalance = 12350.00m,
            Currency = "USD",
            IsActive = true,
            CreatedAt = Now.AddMonths(-2),
            UpdatedAt = Now
        };
        context.Accounts.Add(premiumChecking);
        accounts["premiumChecking"] = premiumChecking;

        var premiumSavings = new Account
        {
            UserId = users["premium"].Id,
            PlaidAccountId = "manual-premium-savings",
            Name = "High-Yield Savings",
            OfficialName = "Marcus Savings Account",
            Mask = "8765",
            Type = AccountType.Savings,
            Subtype = "savings",
            CurrentBalance = 45000.00m,
            AvailableBalance = 45000.00m,
            Currency = "USD",
            IsActive = true,
            CreatedAt = Now.AddMonths(-2),
            UpdatedAt = Now
        };
        context.Accounts.Add(premiumSavings);
        accounts["premiumSavings"] = premiumSavings;

        var premiumCredit = new Account
        {
            UserId = users["premium"].Id,
            PlaidAccountId = "manual-premium-credit",
            Name = "Amex Gold",
            OfficialName = "American Express Gold Card",
            Mask = "2468",
            Type = AccountType.CreditCard,
            Subtype = "credit card",
            CurrentBalance = -2847.32m,
            AvailableBalance = 17152.68m,
            Currency = "USD",
            IsActive = true,
            CreatedAt = Now.AddMonths(-2),
            UpdatedAt = Now
        };
        context.Accounts.Add(premiumCredit);
        accounts["premiumCredit"] = premiumCredit;

        // Family shared accounts
        if (family != null && users.ContainsKey("familyOwner"))
        {
            var familyChecking = new Account
            {
                UserId = users["familyOwner"].Id,
                FamilyId = family.Id,
                PlaidAccountId = "manual-family-checking",
                Name = "Family Checking",
                OfficialName = "Joint Checking Account",
                Mask = "3456",
                Type = AccountType.Checking,
                Subtype = "checking",
                CurrentBalance = 8750.00m,
                AvailableBalance = 8700.00m,
                Currency = "USD",
                IsActive = true,
                CreatedAt = Now.AddMonths(-5),
                UpdatedAt = Now
            };
            context.Accounts.Add(familyChecking);
            accounts["familyChecking"] = familyChecking;

            var familySavings = new Account
            {
                UserId = users["familyOwner"].Id,
                FamilyId = family.Id,
                PlaidAccountId = "manual-family-savings",
                Name = "Family Savings",
                OfficialName = "Family Emergency Fund",
                Mask = "7890",
                Type = AccountType.Savings,
                Subtype = "savings",
                CurrentBalance = 25000.00m,
                AvailableBalance = 25000.00m,
                Currency = "USD",
                IsActive = true,
                CreatedAt = Now.AddMonths(-5),
                UpdatedAt = Now
            };
            context.Accounts.Add(familySavings);
            accounts["familySavings"] = familySavings;
        }

        await context.SaveChangesAsync();
        Console.WriteLine($"Seeded {accounts.Count} test accounts");

        return accounts;
    }

    private static async Task SeedTestTransactions(BuoyAIDbContext context, Dictionary<string, Account> accounts)
    {
        Console.WriteLine("Seeding test transactions...");

        var transactions = new List<Transaction>();
        var random = new Random(42); // Fixed seed for reproducible data

        // Helper to create transaction
        Transaction CreateTransaction(
            Account account, 
            decimal amount, 
            TransactionType type,
            string description,
            string merchantName,
            TransactionCategory category,
            DateTime date)
        {
            return new Transaction
            {
                AccountId = account.Id,
                PlaidTransactionId = $"manual-{Guid.NewGuid():N}",
                Amount = Math.Abs(amount),
                Type = type,
                Description = description,
                MerchantName = merchantName,
                CategoryId = (int)category,
                Date = date,
                IsPending = false,
                IsUserCategorized = false,
                CategorySource = "Seeded",
                CreatedAt = date,
                UpdatedAt = date
            };
        }

        // Trial user transactions (last 60 days)
        if (accounts.TryGetValue("trialChecking", out var trialChecking))
        {
            // Income - Salary (bi-weekly)
            transactions.Add(CreateTransaction(trialChecking, 3500.00m, TransactionType.Credit, 
                "ACME Corp Payroll", "ACME Corp", TransactionCategory.Salary, Now.AddDays(-1)));
            transactions.Add(CreateTransaction(trialChecking, 3500.00m, TransactionType.Credit, 
                "ACME Corp Payroll", "ACME Corp", TransactionCategory.Salary, Now.AddDays(-15)));
            transactions.Add(CreateTransaction(trialChecking, 3500.00m, TransactionType.Credit, 
                "ACME Corp Payroll", "ACME Corp", TransactionCategory.Salary, Now.AddDays(-29)));
            transactions.Add(CreateTransaction(trialChecking, 3500.00m, TransactionType.Credit, 
                "ACME Corp Payroll", "ACME Corp", TransactionCategory.Salary, Now.AddDays(-43)));

            // Groceries
            transactions.Add(CreateTransaction(trialChecking, 156.32m, TransactionType.Debit, 
                "WHOLE FOODS MKT", "Whole Foods", TransactionCategory.Groceries, Now.AddDays(-2)));
            transactions.Add(CreateTransaction(trialChecking, 87.45m, TransactionType.Debit, 
                "TRADER JOES", "Trader Joe's", TransactionCategory.Groceries, Now.AddDays(-9)));
            transactions.Add(CreateTransaction(trialChecking, 124.89m, TransactionType.Debit, 
                "COSTCO WHOLESALE", "Costco", TransactionCategory.Groceries, Now.AddDays(-16)));
            transactions.Add(CreateTransaction(trialChecking, 62.31m, TransactionType.Debit, 
                "SAFEWAY", "Safeway", TransactionCategory.Groceries, Now.AddDays(-23)));

            // Dining Out
            transactions.Add(CreateTransaction(trialChecking, 45.67m, TransactionType.Debit, 
                "CHIPOTLE", "Chipotle", TransactionCategory.DiningOut, Now.AddDays(-3)));
            transactions.Add(CreateTransaction(trialChecking, 78.90m, TransactionType.Debit, 
                "THE CHEESECAKE FACTORY", "Cheesecake Factory", TransactionCategory.DiningOut, Now.AddDays(-7)));
            transactions.Add(CreateTransaction(trialChecking, 23.45m, TransactionType.Debit, 
                "STARBUCKS", "Starbucks", TransactionCategory.DiningOut, Now.AddDays(-5)));
            transactions.Add(CreateTransaction(trialChecking, 34.56m, TransactionType.Debit, 
                "PANERA BREAD", "Panera Bread", TransactionCategory.DiningOut, Now.AddDays(-12)));

            // Utilities
            transactions.Add(CreateTransaction(trialChecking, 125.00m, TransactionType.Debit, 
                "PG&E ELECTRIC", "PG&E", TransactionCategory.Utilities, Now.AddDays(-5)));
            transactions.Add(CreateTransaction(trialChecking, 65.00m, TransactionType.Debit, 
                "COMCAST INTERNET", "Comcast", TransactionCategory.Utilities, Now.AddDays(-10)));

            // Transportation
            transactions.Add(CreateTransaction(trialChecking, 52.34m, TransactionType.Debit, 
                "SHELL OIL", "Shell", TransactionCategory.Transportation, Now.AddDays(-4)));
            transactions.Add(CreateTransaction(trialChecking, 48.76m, TransactionType.Debit, 
                "CHEVRON", "Chevron", TransactionCategory.Transportation, Now.AddDays(-18)));

            // Entertainment
            transactions.Add(CreateTransaction(trialChecking, 15.99m, TransactionType.Debit, 
                "NETFLIX", "Netflix", TransactionCategory.Subscriptions, Now.AddDays(-8)));
            transactions.Add(CreateTransaction(trialChecking, 22.99m, TransactionType.Debit, 
                "SPOTIFY PREMIUM", "Spotify", TransactionCategory.Subscriptions, Now.AddDays(-12)));

            // Shopping
            transactions.Add(CreateTransaction(trialChecking, 89.99m, TransactionType.Debit, 
                "AMAZON.COM", "Amazon", TransactionCategory.Shopping, Now.AddDays(-6)));
            transactions.Add(CreateTransaction(trialChecking, 156.00m, TransactionType.Debit, 
                "TARGET", "Target", TransactionCategory.Shopping, Now.AddDays(-14)));

            // Rent
            transactions.Add(CreateTransaction(trialChecking, 1800.00m, TransactionType.Debit, 
                "RENT PAYMENT - APT 101", "Landlord", TransactionCategory.Rent, Now.AddDays(-1)));
        }

        // Credit card transactions
        if (accounts.TryGetValue("trialCredit", out var trialCredit))
        {
            transactions.Add(CreateTransaction(trialCredit, 234.56m, TransactionType.Debit, 
                "BEST BUY", "Best Buy", TransactionCategory.Shopping, Now.AddDays(-3)));
            transactions.Add(CreateTransaction(trialCredit, 67.89m, TransactionType.Debit, 
                "CVS PHARMACY", "CVS", TransactionCategory.Healthcare, Now.AddDays(-8)));
            transactions.Add(CreateTransaction(trialCredit, 45.00m, TransactionType.Debit, 
                "UBER TRIP", "Uber", TransactionCategory.Transportation, Now.AddDays(-11)));
        }

        // Premium user transactions
        if (accounts.TryGetValue("premiumChecking", out var premiumChecking))
        {
            // Income - Higher salary (bi-weekly)
            transactions.Add(CreateTransaction(premiumChecking, 5000.00m, TransactionType.Credit, 
                "TECH CORP PAYROLL", "Tech Corp", TransactionCategory.Salary, Now.AddDays(-1)));
            transactions.Add(CreateTransaction(premiumChecking, 5000.00m, TransactionType.Credit, 
                "TECH CORP PAYROLL", "Tech Corp", TransactionCategory.Salary, Now.AddDays(-15)));
            transactions.Add(CreateTransaction(premiumChecking, 5000.00m, TransactionType.Credit, 
                "TECH CORP PAYROLL", "Tech Corp", TransactionCategory.Salary, Now.AddDays(-29)));
            transactions.Add(CreateTransaction(premiumChecking, 5000.00m, TransactionType.Credit, 
                "TECH CORP PAYROLL", "Tech Corp", TransactionCategory.Salary, Now.AddDays(-43)));

            // Side income
            transactions.Add(CreateTransaction(premiumChecking, 1500.00m, TransactionType.Credit, 
                "CONSULTING FEE", "Freelance Client", TransactionCategory.OtherIncome, Now.AddDays(-10)));

            // Groceries (higher budget)
            transactions.Add(CreateTransaction(premiumChecking, 245.67m, TransactionType.Debit, 
                "WHOLE FOODS MKT", "Whole Foods", TransactionCategory.Groceries, Now.AddDays(-2)));
            transactions.Add(CreateTransaction(premiumChecking, 178.90m, TransactionType.Debit, 
                "TRADER JOES", "Trader Joe's", TransactionCategory.Groceries, Now.AddDays(-8)));
            transactions.Add(CreateTransaction(premiumChecking, 312.45m, TransactionType.Debit, 
                "COSTCO WHOLESALE", "Costco", TransactionCategory.Groceries, Now.AddDays(-15)));

            // Dining Out (more frequent)
            transactions.Add(CreateTransaction(premiumChecking, 125.00m, TransactionType.Debit, 
                "NOBU RESTAURANT", "Nobu", TransactionCategory.DiningOut, Now.AddDays(-3)));
            transactions.Add(CreateTransaction(premiumChecking, 89.50m, TransactionType.Debit, 
                "BLUE BOTTLE COFFEE", "Blue Bottle", TransactionCategory.DiningOut, Now.AddDays(-5)));
            transactions.Add(CreateTransaction(premiumChecking, 156.78m, TransactionType.Debit, 
                "THE FRENCH LAUNDRY", "French Laundry", TransactionCategory.DiningOut, Now.AddDays(-12)));

            // Utilities
            transactions.Add(CreateTransaction(premiumChecking, 185.00m, TransactionType.Debit, 
                "PG&E ELECTRIC", "PG&E", TransactionCategory.Utilities, Now.AddDays(-5)));
            transactions.Add(CreateTransaction(premiumChecking, 95.00m, TransactionType.Debit, 
                "COMCAST INTERNET", "Comcast", TransactionCategory.Utilities, Now.AddDays(-10)));
            transactions.Add(CreateTransaction(premiumChecking, 45.00m, TransactionType.Debit, 
                "WATER COMPANY", "City Water", TransactionCategory.Utilities, Now.AddDays(-7)));

            // Mortgage (not rent)
            transactions.Add(CreateTransaction(premiumChecking, 2800.00m, TransactionType.Debit, 
                "MORTGAGE PAYMENT", "Wells Fargo Mortgage", TransactionCategory.Rent, Now.AddDays(-1)));

            // Transportation
            transactions.Add(CreateTransaction(premiumChecking, 78.50m, TransactionType.Debit, 
                "TESLA SUPERCHARGER", "Tesla", TransactionCategory.Transportation, Now.AddDays(-4)));
            transactions.Add(CreateTransaction(premiumChecking, 450.00m, TransactionType.Debit, 
                "AUTO INSURANCE", "State Farm", TransactionCategory.Transportation, Now.AddDays(-20)));

            // Subscriptions
            transactions.Add(CreateTransaction(premiumChecking, 22.99m, TransactionType.Debit, 
                "NETFLIX PREMIUM", "Netflix", TransactionCategory.Subscriptions, Now.AddDays(-8)));
            transactions.Add(CreateTransaction(premiumChecking, 14.99m, TransactionType.Debit, 
                "SPOTIFY FAMILY", "Spotify", TransactionCategory.Subscriptions, Now.AddDays(-12)));
            transactions.Add(CreateTransaction(premiumChecking, 54.99m, TransactionType.Debit, 
                "ADOBE CREATIVE", "Adobe", TransactionCategory.Subscriptions, Now.AddDays(-15)));

            // Investment/Savings transfers
            transactions.Add(CreateTransaction(premiumChecking, 2000.00m, TransactionType.Debit, 
                "TRANSFER TO SAVINGS", "Internal Transfer", TransactionCategory.Transfer, Now.AddDays(-2)));
            transactions.Add(CreateTransaction(premiumChecking, 500.00m, TransactionType.Debit, 
                "ROBINHOOD DEPOSIT", "Robinhood", TransactionCategory.Investment, Now.AddDays(-3)));
        }

        // Premium credit card transactions
        if (accounts.TryGetValue("premiumCredit", out var premiumCredit))
        {
            transactions.Add(CreateTransaction(premiumCredit, 899.00m, TransactionType.Debit, 
                "APPLE STORE", "Apple", TransactionCategory.Shopping, Now.AddDays(-5)));
            transactions.Add(CreateTransaction(premiumCredit, 234.56m, TransactionType.Debit, 
                "NORDSTROM", "Nordstrom", TransactionCategory.Shopping, Now.AddDays(-9)));
            transactions.Add(CreateTransaction(premiumCredit, 150.00m, TransactionType.Debit, 
                "SPA TREATMENT", "Burke Williams", TransactionCategory.PersonalCare, Now.AddDays(-14)));
            transactions.Add(CreateTransaction(premiumCredit, 350.00m, TransactionType.Debit, 
                "UNITED AIRLINES", "United Airlines", TransactionCategory.Travel, Now.AddDays(-21)));
        }

        // Family account transactions
        if (accounts.TryGetValue("familyChecking", out var familyChecking))
        {
            // Family income
            transactions.Add(CreateTransaction(familyChecking, 5500.00m, TransactionType.Credit, 
                "EMPLOYER DIRECT DEPOSIT", "Employer", TransactionCategory.Salary, Now.AddDays(-1)));
            transactions.Add(CreateTransaction(familyChecking, 5500.00m, TransactionType.Credit, 
                "EMPLOYER DIRECT DEPOSIT", "Employer", TransactionCategory.Salary, Now.AddDays(-15)));

            // Family groceries
            transactions.Add(CreateTransaction(familyChecking, 287.45m, TransactionType.Debit, 
                "COSTCO WHOLESALE", "Costco", TransactionCategory.Groceries, Now.AddDays(-4)));
            transactions.Add(CreateTransaction(familyChecking, 198.32m, TransactionType.Debit, 
                "WHOLE FOODS MKT", "Whole Foods", TransactionCategory.Groceries, Now.AddDays(-11)));

            // Family utilities
            transactions.Add(CreateTransaction(familyChecking, 185.00m, TransactionType.Debit, 
                "PG&E ELECTRIC", "PG&E", TransactionCategory.Utilities, Now.AddDays(-6)));

            // Kids activities
            transactions.Add(CreateTransaction(familyChecking, 150.00m, TransactionType.Debit, 
                "YMCA MEMBERSHIP", "YMCA", TransactionCategory.Healthcare, Now.AddDays(-2)));
            transactions.Add(CreateTransaction(familyChecking, 75.00m, TransactionType.Debit, 
                "PIANO LESSONS", "Music School", TransactionCategory.Education, Now.AddDays(-9)));
        }

        context.Transactions.AddRange(transactions);
        await context.SaveChangesAsync();

        Console.WriteLine($"Seeded {transactions.Count} test transactions");
    }

    private static async Task SeedTestBudgets(
        BuoyAIDbContext context, 
        Dictionary<string, User> users,
        Family? family)
    {
        Console.WriteLine("Seeding test budgets...");

        try
        {
            var periodStart = new DateTime(Now.Year, Now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var periodEnd = new DateTime(Now.Year, Now.Month, DateTime.DaysInMonth(Now.Year, Now.Month), 23, 59, 59, DateTimeKind.Utc);

            // Personal budget for trial user
            var trialBudget = new Budget
            {
                UserId = users["trial"].Id,
                Name = "Monthly Budget",
                Period = BudgetPeriod.Monthly,
                CurrentPeriodStart = periodStart,
                CurrentPeriodEnd = periodEnd,
                LastReset = periodStart,
                AutoReset = true,
                TotalIncome = 7000.00m,
                TotalExpenses = 4500.00m,
                IsActive = true,
                Type = BudgetType.Personal,
                IsPrimary = true,
                CreatedAt = Now.AddDays(-10),
                UpdatedAt = Now
            };
            context.Budgets.Add(trialBudget);
            await context.SaveChangesAsync();
            Console.WriteLine($"Created trial budget with ID: {trialBudget.Id}");

            // Budget categories for trial user
            var trialCategories = new List<BudgetCategory>
            {
                CreateBudgetCategory(trialBudget.Id, TransactionCategory.Salary, true, 7000.00m, 7000.00m, 0),
                CreateBudgetCategory(trialBudget.Id, TransactionCategory.Rent, false, 1800.00m, 1800.00m, 1),
                CreateBudgetCategory(trialBudget.Id, TransactionCategory.Groceries, false, 600.00m, 430.97m, 2),
                CreateBudgetCategory(trialBudget.Id, TransactionCategory.DiningOut, false, 300.00m, 182.58m, 3),
                CreateBudgetCategory(trialBudget.Id, TransactionCategory.Utilities, false, 200.00m, 190.00m, 4),
                CreateBudgetCategory(trialBudget.Id, TransactionCategory.Transportation, false, 200.00m, 101.10m, 5),
                CreateBudgetCategory(trialBudget.Id, TransactionCategory.Shopping, false, 400.00m, 245.99m, 6),
                CreateBudgetCategory(trialBudget.Id, TransactionCategory.Subscriptions, false, 100.00m, 38.98m, 7),
                CreateBudgetCategory(trialBudget.Id, TransactionCategory.Healthcare, false, 150.00m, 67.89m, 8),
                CreateBudgetCategory(trialBudget.Id, TransactionCategory.Entertainment, false, 200.00m, 0.00m, 9)
            };
            context.BudgetCategories.AddRange(trialCategories);
            await context.SaveChangesAsync();
            Console.WriteLine($"Created {trialCategories.Count} categories for trial budget");

            // Premium user budget (higher income/expenses)
            var premiumBudget = new Budget
            {
                UserId = users["premium"].Id,
                Name = "Premium Monthly Budget",
                Period = BudgetPeriod.Monthly,
                CurrentPeriodStart = periodStart,
                CurrentPeriodEnd = periodEnd,
                LastReset = periodStart,
                AutoReset = true,
                TotalIncome = 11500.00m,
                TotalExpenses = 8500.00m,
                IsActive = true,
                Type = BudgetType.Personal,
                IsPrimary = true,
                CreatedAt = Now.AddMonths(-2),
                UpdatedAt = Now
            };
            context.Budgets.Add(premiumBudget);
            await context.SaveChangesAsync();
            Console.WriteLine($"Created premium budget with ID: {premiumBudget.Id}");

            // Budget categories for premium user
            var premiumCategories = new List<BudgetCategory>
            {
                CreateBudgetCategory(premiumBudget.Id, TransactionCategory.Salary, true, 10000.00m, 10000.00m, 0),
                CreateBudgetCategory(premiumBudget.Id, TransactionCategory.OtherIncome, true, 1500.00m, 1500.00m, 1),
                CreateBudgetCategory(premiumBudget.Id, TransactionCategory.Rent, false, 2800.00m, 2800.00m, 2),
                CreateBudgetCategory(premiumBudget.Id, TransactionCategory.Groceries, false, 800.00m, 737.02m, 3),
                CreateBudgetCategory(premiumBudget.Id, TransactionCategory.DiningOut, false, 500.00m, 371.28m, 4),
                CreateBudgetCategory(premiumBudget.Id, TransactionCategory.Utilities, false, 350.00m, 325.00m, 5),
                CreateBudgetCategory(premiumBudget.Id, TransactionCategory.Transportation, false, 600.00m, 528.50m, 6),
                CreateBudgetCategory(premiumBudget.Id, TransactionCategory.Shopping, false, 500.00m, 1133.56m, 7),
                CreateBudgetCategory(premiumBudget.Id, TransactionCategory.Subscriptions, false, 150.00m, 92.97m, 8),
                CreateBudgetCategory(premiumBudget.Id, TransactionCategory.Transfer, false, 2000.00m, 2000.00m, 9),
                CreateBudgetCategory(premiumBudget.Id, TransactionCategory.Investment, false, 500.00m, 500.00m, 10),
                CreateBudgetCategory(premiumBudget.Id, TransactionCategory.Travel, false, 400.00m, 350.00m, 11),
                CreateBudgetCategory(premiumBudget.Id, TransactionCategory.PersonalCare, false, 200.00m, 150.00m, 12)
            };
            context.BudgetCategories.AddRange(premiumCategories);
            await context.SaveChangesAsync();
            Console.WriteLine($"Created {premiumCategories.Count} categories for premium budget");

            // Family budget
            if (family != null && users.ContainsKey("familyOwner"))
            {
                var familyBudget = new Budget
                {
                    UserId = users["familyOwner"].Id,
                    FamilyId = family.Id,
                    Name = "Family Budget",
                    Period = BudgetPeriod.Monthly,
                    CurrentPeriodStart = periodStart,
                    CurrentPeriodEnd = periodEnd,
                    LastReset = periodStart,
                    AutoReset = true,
                    TotalIncome = 11000.00m,
                    TotalExpenses = 7500.00m,
                    IsActive = true,
                    Type = BudgetType.Family,
                    IsPrimary = true,
                    CreatedAt = Now.AddMonths(-5),
                    UpdatedAt = Now
                };
                context.Budgets.Add(familyBudget);
                await context.SaveChangesAsync();
                Console.WriteLine($"Created family budget with ID: {familyBudget.Id}");

                var familyCategories = new List<BudgetCategory>
                {
                    CreateBudgetCategory(familyBudget.Id, TransactionCategory.Salary, true, 11000.00m, 11000.00m, 0),
                    CreateBudgetCategory(familyBudget.Id, TransactionCategory.Rent, false, 2500.00m, 2500.00m, 1),
                    CreateBudgetCategory(familyBudget.Id, TransactionCategory.Groceries, false, 1200.00m, 485.77m, 2),
                    CreateBudgetCategory(familyBudget.Id, TransactionCategory.Utilities, false, 350.00m, 185.00m, 3),
                    CreateBudgetCategory(familyBudget.Id, TransactionCategory.Education, false, 500.00m, 75.00m, 4),
                    CreateBudgetCategory(familyBudget.Id, TransactionCategory.Healthcare, false, 300.00m, 150.00m, 5)
                };
                context.BudgetCategories.AddRange(familyCategories);
                await context.SaveChangesAsync();
                Console.WriteLine($"Created {familyCategories.Count} categories for family budget");

                // Child allowance budget
                var allowanceBudget = new Budget
                {
                    UserId = users["familyReadOnly"].Id,
                    FamilyId = family.Id,
                    ParentBudgetId = familyBudget.Id,
                    Name = "Weekly Allowance",
                    Period = BudgetPeriod.Weekly,
                    CurrentPeriodStart = Now.AddDays(-(int)Now.DayOfWeek),
                    CurrentPeriodEnd = Now.AddDays(6 - (int)Now.DayOfWeek),
                    LastReset = Now.AddDays(-(int)Now.DayOfWeek),
                    AutoReset = true,
                    AllocatedAmount = 50.00m,
                    TotalIncome = 50.00m,
                    TotalExpenses = 25.00m,
                    IsActive = true,
                    Type = BudgetType.Allowance,
                    IsPrimary = false,
                    CreatedAt = Now.AddMonths(-2),
                    UpdatedAt = Now
                };
                context.Budgets.Add(allowanceBudget);
                await context.SaveChangesAsync();
                Console.WriteLine($"Created allowance budget with ID: {allowanceBudget.Id}");
            }

            Console.WriteLine("Seeded test budgets with categories");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR seeding budgets: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    // Helper method to create budget categories with all required fields
    // Note: SpentAmount removed from entity - spending is calculated from transactions
    private static BudgetCategory CreateBudgetCategory(int budgetId, TransactionCategory category, bool isIncome, decimal allocated, decimal spent, int sortOrder)
    {
        return new BudgetCategory
        {
            BudgetId = budgetId,
            CategoryId = (int)category,
            IsIncome = isIncome,
            AllocatedAmount = allocated,
            // SpentAmount is derived from transactions, not stored
            SortOrder = sortOrder,
            LastAllowanceCalculation = Now,
            CreatedAt = Now,
            UpdatedAt = Now
        };
    }

    private static async Task SeedTestSubscriptions(BuoyAIDbContext context, Dictionary<string, User> users)
    {
        Console.WriteLine("Seeding test subscriptions...");

        var subscriptions = new List<Subscription>
        {
            // Trial user subscriptions
            new Subscription
            {
                UserId = users["trial"].Id,
                Name = "Netflix",
                Description = "Streaming service",
                Amount = 15.99m,
                Frequency = SubscriptionFrequency.Monthly,
                NextDueDate = Now.AddDays(22),
                LastBilledDate = Now.AddDays(-8),
                Status = SubscriptionStatus.Active,
                Category = SubscriptionCategory.Streaming,
                IsDetected = true,
                MerchantName = "Netflix",
                CreatedAt = Now.AddMonths(-6),
                UpdatedAt = Now
            },
            new Subscription
            {
                UserId = users["trial"].Id,
                Name = "Spotify Premium",
                Description = "Music streaming",
                Amount = 9.99m,
                Frequency = SubscriptionFrequency.Monthly,
                NextDueDate = Now.AddDays(18),
                LastBilledDate = Now.AddDays(-12),
                Status = SubscriptionStatus.Active,
                Category = SubscriptionCategory.Streaming,
                IsDetected = true,
                MerchantName = "Spotify",
                CreatedAt = Now.AddMonths(-8),
                UpdatedAt = Now
            },
            new Subscription
            {
                UserId = users["trial"].Id,
                Name = "Adobe Creative Cloud",
                Description = "Design software",
                Amount = 54.99m,
                Frequency = SubscriptionFrequency.Monthly,
                NextDueDate = Now.AddDays(5),
                LastBilledDate = Now.AddDays(-25),
                Status = SubscriptionStatus.Active,
                Category = SubscriptionCategory.Software,
                IsDetected = false,
                MerchantName = "Adobe",
                CreatedAt = Now.AddMonths(-12),
                UpdatedAt = Now
            },
            new Subscription
            {
                UserId = users["trial"].Id,
                Name = "Gym Membership",
                Description = "Planet Fitness",
                Amount = 24.99m,
                Frequency = SubscriptionFrequency.Monthly,
                NextDueDate = Now.AddDays(10),
                LastBilledDate = Now.AddDays(-20),
                Status = SubscriptionStatus.Active,
                Category = SubscriptionCategory.Fitness,
                IsDetected = false,
                MerchantName = "Planet Fitness",
                CreatedAt = Now.AddMonths(-4),
                UpdatedAt = Now
            }
        };

        context.Subscriptions.AddRange(subscriptions);
        await context.SaveChangesAsync();

        Console.WriteLine($"Seeded {subscriptions.Count} test subscriptions");
    }

    private static async Task SeedTestSavingsGoals(
        BuoyAIDbContext context, 
        Dictionary<string, User> users,
        Family? family)
    {
        Console.WriteLine("Seeding test savings goals...");

        var goals = new List<SavingsGoal>
        {
            // Trial user goals
            new SavingsGoal
            {
                UserId = users["trial"].Id,
                Type = SavingsGoalType.Personal,
                Name = "Emergency Fund",
                Description = "6 months of expenses",
                TargetAmount = 15000.00m,
                CurrentAmount = 5500.00m,
                AllocationPercentage = 10.0m,
                TargetDate = Now.AddMonths(18),
                IconName = "shield",
                ColorTheme = "danger",
                IsActive = true,
                IsCompleted = false,
                CreatedAt = Now.AddMonths(-3),
                UpdatedAt = Now
            },
            new SavingsGoal
            {
                UserId = users["trial"].Id,
                Type = SavingsGoalType.Personal,
                Name = "Vacation Fund",
                Description = "Trip to Japan",
                TargetAmount = 5000.00m,
                CurrentAmount = 1250.00m,
                AllocationPercentage = 5.0m,
                TargetDate = Now.AddMonths(8),
                IconName = "airplane",
                ColorTheme = "info",
                IsActive = true,
                IsCompleted = false,
                CreatedAt = Now.AddMonths(-2),
                UpdatedAt = Now
            }
        };

        // Family savings goals
        if (family != null && users.ContainsKey("familyOwner"))
        {
            goals.Add(new SavingsGoal
            {
                UserId = users["familyOwner"].Id,
                FamilyId = family.Id,
                Type = SavingsGoalType.Family,
                Name = "College Fund",
                Description = "Kids' education savings",
                TargetAmount = 50000.00m,
                CurrentAmount = 12500.00m,
                AllocationPercentage = 15.0m,
                TargetDate = Now.AddYears(10),
                IconName = "graduation-cap",
                ColorTheme = "success",
                IsActive = true,
                IsCompleted = false,
                CreatedAt = Now.AddMonths(-24),
                UpdatedAt = Now
            });

            goals.Add(new SavingsGoal
            {
                UserId = users["familyOwner"].Id,
                FamilyId = family.Id,
                Type = SavingsGoalType.Family,
                Name = "Home Down Payment",
                Description = "Save for a house",
                TargetAmount = 80000.00m,
                CurrentAmount = 22000.00m,
                AllocationPercentage = 20.0m,
                TargetDate = Now.AddYears(3),
                IconName = "home",
                ColorTheme = "primary",
                IsActive = true,
                IsCompleted = false,
                CreatedAt = Now.AddMonths(-18),
                UpdatedAt = Now
            });
        }

        context.SavingsGoals.AddRange(goals);
        await context.SaveChangesAsync();

        Console.WriteLine($"Seeded {goals.Count} test savings goals");
    }

    private static async Task SeedTestNotifications(BuoyAIDbContext context, Dictionary<string, User> users)
    {
        Console.WriteLine("Seeding test notifications...");

        var notifications = new List<Notification>
        {
            // Trial user notifications
            new Notification
            {
                UserId = users["trial"].Id,
                Type = "budget_alert",
                Title = "Budget Alert",
                Message = "You've spent 85% of your Dining Out budget for this month.",
                ActionUrl = "/budget",
                ActionText = "View Budget",
                IsRead = false,
                CreatedAt = Now.AddHours(-2),
                UpdatedAt = Now.AddHours(-2)
            },
            new Notification
            {
                UserId = users["trial"].Id,
                Type = "subscription_due",
                Title = "Upcoming Payment",
                Message = "Your Adobe Creative Cloud subscription ($54.99) is due in 5 days.",
                ActionUrl = "/subscriptions",
                ActionText = "View Subscriptions",
                IsRead = false,
                CreatedAt = Now.AddDays(-1),
                UpdatedAt = Now.AddDays(-1)
            },
            new Notification
            {
                UserId = users["trial"].Id,
                Type = "savings_milestone",
                Title = "Savings Milestone!",
                Message = "Congratulations! Your Emergency Fund has reached 33% of your goal.",
                ActionUrl = "/savings",
                ActionText = "View Goals",
                IsRead = true,
                ReadAt = Now.AddDays(-2),
                CreatedAt = Now.AddDays(-3),
                UpdatedAt = Now.AddDays(-2)
            },
            new Notification
            {
                UserId = users["trial"].Id,
                Type = "welcome",
                Title = "Welcome to Buoy Money!",
                Message = "Thanks for joining! Connect your bank accounts to get started with automatic transaction tracking.",
                ActionUrl = "/accounts",
                ActionText = "Connect Accounts",
                IsRead = true,
                ReadAt = Now.AddDays(-9),
                CreatedAt = Now.AddDays(-10),
                UpdatedAt = Now.AddDays(-9)
            }
        };

        // Family notifications
        if (users.ContainsKey("familyOwner"))
        {
            notifications.Add(new Notification
            {
                UserId = users["familyOwner"].Id,
                Type = "family_activity",
                Title = "Family Member Activity",
                Message = "Family Member logged in to the family account.",
                IsRead = false,
                CreatedAt = Now.AddHours(-5),
                UpdatedAt = Now.AddHours(-5)
            });
        }

        context.Notifications.AddRange(notifications);
        await context.SaveChangesAsync();

        Console.WriteLine($"Seeded {notifications.Count} test notifications");
    }

    /// <summary>
    /// Seeds diverse subscription-like transaction patterns for testing subscription detection algorithms.
    /// These patterns cover various edge cases:
    /// 1. Perfect monthly (same day, same amount) - Netflix-like
    /// 2. Monthly with day drift (±1-3 days) - Spotify-like
    /// 3. Monthly with amount variation - utility-like
    /// 4. Weekly exact interval - gym/class membership
    /// 5. Annual subscription - domain registrar
    /// 6. Sparse data (only 2 occurrences) - too few to confirm
    /// 7. False positive (same merchant, random amounts) - grocery store
    /// 8. Recently started (2 months only) - new subscription
    /// </summary>
    private static async Task SeedSubscriptionDetectionTestPatterns(
        BuoyAIDbContext context,
        Dictionary<string, Account> accounts)
    {
        Console.WriteLine("Seeding subscription detection test patterns...");

        if (!accounts.TryGetValue("premiumChecking", out var premiumChecking))
        {
            Console.WriteLine("Premium checking account not found, skipping subscription patterns");
            return;
        }

        var transactions = new List<Transaction>();
        
        // Helper to create transaction
        Transaction CreateTransaction(
            int accountId,
            decimal amount,
            string description,
            string merchantName,
            TransactionCategory category,
            DateTime date)
        {
            return new Transaction
            {
                AccountId = accountId,
                PlaidTransactionId = $"sub-pattern-{Guid.NewGuid():N}",
                Amount = Math.Abs(amount),
                Type = TransactionType.Debit,
                Description = description,
                MerchantName = merchantName,
                CategoryId = (int)category,
                Date = date,
                IsPending = false,
                IsUserCategorized = false,
                CategorySource = "Seeded",
                CreatedAt = date,
                UpdatedAt = date
            };
        }

        // ===== PATTERN 1: Perfect Monthly (same day, exact amount) =====
        // Disney+ - $7.99 on the 15th of each month for 8 months
        // Should have HIGH confidence (0.95+)
        for (int i = 0; i < 8; i++)
        {
            var date = Now.AddMonths(-i).Date.AddDays(-(Now.Day - 15)); // 15th of each month
            if (date > Now) date = date.AddMonths(-1);
            transactions.Add(CreateTransaction(
                premiumChecking.Id, 7.99m,
                "DISNEY PLUS", "Disney+",
                TransactionCategory.Subscriptions, date));
        }

        // ===== PATTERN 2: Monthly with Day Drift (±1-3 days) =====
        // Hulu - $17.99 around the 5th (varies by 1-3 days) for 6 months
        // Should have GOOD confidence (0.85+)
        var dayDrifts = new[] { 0, 1, -1, 2, -2, 1 };
        for (int i = 0; i < 6; i++)
        {
            var baseDate = Now.AddMonths(-i).Date.AddDays(-(Now.Day - 5)); // 5th of each month
            if (baseDate > Now) baseDate = baseDate.AddMonths(-1);
            var driftedDate = baseDate.AddDays(dayDrifts[i]);
            transactions.Add(CreateTransaction(
                premiumChecking.Id, 17.99m,
                "HULU LLC", "Hulu",
                TransactionCategory.Subscriptions, driftedDate));
        }

        // ===== PATTERN 3: Monthly with Amount Variation (utility-like) =====
        // Verizon - varies between $85-$105 monthly for 10 months
        // Should have MEDIUM confidence (0.75-0.85)
        var utilityAmounts = new[] { 89.99m, 94.50m, 87.25m, 102.30m, 91.45m, 88.00m, 99.99m, 95.50m, 103.25m, 86.75m };
        for (int i = 0; i < 10; i++)
        {
            var date = Now.AddMonths(-i).Date.AddDays(-(Now.Day - 22)); // 22nd of each month
            if (date > Now) date = date.AddMonths(-1);
            transactions.Add(CreateTransaction(
                premiumChecking.Id, utilityAmounts[i],
                "VERIZON WIRELESS", "Verizon",
                TransactionCategory.Utilities, date));
        }

        // ===== PATTERN 4: Weekly Exact Interval =====
        // CrossFit Gym - $35.00 every Monday for 12 weeks
        // Should have HIGH confidence (0.90+)
        for (int i = 0; i < 12; i++)
        {
            // Get most recent Monday, then go back i weeks
            var daysSinceMonday = ((int)Now.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            var lastMonday = Now.Date.AddDays(-daysSinceMonday);
            var date = lastMonday.AddDays(-7.0 * i);
            transactions.Add(CreateTransaction(
                premiumChecking.Id, 35.00m,
                "CROSSFIT BOX MEMBERSHIP", "CrossFit Box",
                TransactionCategory.PersonalCare, date));
        }

        // ===== PATTERN 5: Annual Subscription =====
        // GoDaddy Domain - $18.99 once per year for 3 years
        // Should have MEDIUM confidence (harder to detect, only 3 data points but regular)
        for (int i = 0; i < 3; i++)
        {
            var date = Now.AddYears(-i).Date.AddDays(-(Now.DayOfYear - 45)); // Feb 14
            if (date > Now) date = date.AddYears(-1);
            transactions.Add(CreateTransaction(
                premiumChecking.Id, 18.99m,
                "GODADDY.COM", "GoDaddy",
                TransactionCategory.Subscriptions, date));
        }

        // ===== PATTERN 6: Sparse Data (insufficient for detection) =====
        // New Yorker Magazine - only 2 occurrences
        // Should have LOW confidence or not be detected at all
        transactions.Add(CreateTransaction(
            premiumChecking.Id, 12.99m,
            "NEW YORKER DIGITAL", "New Yorker",
            TransactionCategory.Entertainment, Now.AddDays(-35)));
        transactions.Add(CreateTransaction(
            premiumChecking.Id, 12.99m,
            "NEW YORKER DIGITAL", "New Yorker",
            TransactionCategory.Entertainment, Now.AddDays(-65)));

        // ===== PATTERN 7: False Positive (same merchant, random amounts) =====
        // Whole Foods - regular shopping but NOT a subscription
        // Should NOT be detected as subscription (amounts too variable)
        var groceryAmounts = new[] { 67.34m, 124.56m, 45.89m, 189.23m, 78.45m, 156.78m, 92.34m, 43.21m };
        var groceryDays = new[] { -3, -8, -14, -21, -27, -35, -42, -50 };
        for (int i = 0; i < groceryAmounts.Length; i++)
        {
            transactions.Add(CreateTransaction(
                premiumChecking.Id, groceryAmounts[i],
                "WHOLE FOODS MARKET", "Whole Foods",
                TransactionCategory.Groceries, Now.AddDays(groceryDays[i])));
        }

        // ===== PATTERN 8: Recently Started (new subscription) =====
        // HBO Max - started 2 months ago, only 2 charges
        // Should have LOW-MEDIUM confidence (pattern emerging)
        transactions.Add(CreateTransaction(
            premiumChecking.Id, 15.99m,
            "HBO MAX", "HBO Max",
            TransactionCategory.Subscriptions, Now.AddDays(-15)));
        transactions.Add(CreateTransaction(
            premiumChecking.Id, 15.99m,
            "HBO MAX", "HBO Max",
            TransactionCategory.Subscriptions, Now.AddDays(-45)));

        // ===== PATTERN 9: Bi-weekly Pattern =====
        // ClassPass - $49 every 2 weeks for 10 occurrences
        // Should have HIGH confidence with BiWeekly frequency detected
        for (int i = 0; i < 10; i++)
        {
            transactions.Add(CreateTransaction(
                premiumChecking.Id, 49.00m,
                "CLASSPASS INC", "ClassPass",
                TransactionCategory.PersonalCare, Now.AddDays(-14.0 * i - 3.0)));
        }

        // ===== PATTERN 10: Quarterly Subscription =====
        // LinkedIn Premium - $59.99 every 3 months for 4 quarters
        // Should detect as Quarterly with MEDIUM-HIGH confidence
        for (int i = 0; i < 4; i++)
        {
            var date = Now.AddMonths(-3 * i).Date.AddDays(-(Now.Day - 10));
            if (date > Now) date = date.AddMonths(-3);
            transactions.Add(CreateTransaction(
                premiumChecking.Id, 59.99m,
                "LINKEDIN PREMIUM", "LinkedIn",
                TransactionCategory.Subscriptions, date));
        }

        context.Transactions.AddRange(transactions);
        await context.SaveChangesAsync();

        Console.WriteLine($"Seeded {transactions.Count} subscription detection test transactions");
        Console.WriteLine("  Pattern 1: Disney+ (8 perfect monthly charges)");
        Console.WriteLine("  Pattern 2: Hulu (6 monthly with day drift)");
        Console.WriteLine("  Pattern 3: Verizon (10 monthly with amount variation)");
        Console.WriteLine("  Pattern 4: CrossFit (12 weekly exact)");
        Console.WriteLine("  Pattern 5: GoDaddy (3 annual)");
        Console.WriteLine("  Pattern 6: New Yorker (2 sparse - should NOT detect)");
        Console.WriteLine("  Pattern 7: Whole Foods (8 random - false positive test)");
        Console.WriteLine("  Pattern 8: HBO Max (2 recent - emerging pattern)");
        Console.WriteLine("  Pattern 9: ClassPass (10 bi-weekly)");
        Console.WriteLine("  Pattern 10: LinkedIn (4 quarterly)");
    }
}
