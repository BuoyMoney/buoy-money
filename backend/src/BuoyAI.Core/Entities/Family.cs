using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Entities;

public class Family
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public User CreatedByUser { get; set; } = null!;
    public ICollection<FamilyMember> Members { get; set; } = new List<FamilyMember>();
    public ICollection<PlaidConnection> PlaidConnections { get; set; } = new List<PlaidConnection>();
    public ICollection<Account> Accounts { get; set; } = new List<Account>();
    public ICollection<Budget> Budgets { get; set; } = new List<Budget>(); // ONE active budget per family
    public ICollection<Subscription> Subscriptions { get; set; } = new List<Subscription>();
}
