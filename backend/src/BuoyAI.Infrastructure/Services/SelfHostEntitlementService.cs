using BuoyAI.Core.Entitlements;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// The entitlement answer on a self-hosted server: there is no billing, so every real account is
/// Premium and every feature is open. Operators on the <c>Admin:Emails</c> list are still Admin
/// (that is a role, not a plan). A user id that does not exist gets no tier, as on hosted.
///
/// This is the service registered when <c>BuoyMoney:Edition=selfhost</c>; it takes no Stripe,
/// subscription or trial state, so it works with a database that has none of those rows.
/// </summary>
public class SelfHostEntitlementService : IEntitlementService
{
    private readonly BuoyAIDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly Lazy<AiLimits> _aiLimits;

    public SelfHostEntitlementService(BuoyAIDbContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
        _aiLimits = new Lazy<AiLimits>(() => AiLimitsConfiguration.Load(configuration));
    }

    public async Task<SubscriptionTier?> GetEffectiveTierAsync(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        return user == null ? null : TierFor(user);
    }

    public async Task<SubscriptionTier> GetTierOrStandardAsync(int userId)
    {
        try
        {
            return await GetEffectiveTierAsync(userId) ?? SubscriptionTier.Standard;
        }
        catch
        {
            return SubscriptionTier.Standard;
        }
    }

    public async Task<bool> HasPremiumAccessAsync(int userId) =>
        (await GetEffectiveTierAsync(userId)).HasPremiumAccess();

    public async Task<bool> HasFeatureAsync(int userId, string feature)
    {
        var tier = await GetEffectiveTierAsync(userId);
        if (tier == null) return false;
        // The admin dashboard is a role. Every other feature is open to every account.
        return feature != "admin_dashboard" || tier == SubscriptionTier.Admin;
    }

    public bool HasPremiumAccess(User user) => true;

    public AiTierLimits GetAiLimits(SubscriptionTier tier) => _aiLimits.Value.For(tier);

    // No billing on a self-hosted server: every real account has every product.
    public async Task<ProductAccess> GetProductAccessAsync(int userId) =>
        await _context.Users.AnyAsync(u => u.Id == userId) ? ProductAccess.All : ProductAccess.None;

    /// <summary>A self-hosted server buys nothing from itself.</summary>
    public Task<bool> HasCloudProductsAsync(int userId) => Task.FromResult(false);

    private SubscriptionTier TierFor(User user) =>
        user.IsInstanceAdmin || AdminPolicy.IsAdminEmail(_configuration, user.Email) ? SubscriptionTier.Admin : SubscriptionTier.Premium;
}
