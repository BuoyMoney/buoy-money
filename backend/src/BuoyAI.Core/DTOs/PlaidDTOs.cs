using BuoyAI.Core.Enums;

namespace BuoyAI.Core.DTOs.Plaid;

public class CreateLinkTokenRequest
{
  public int UserId { get; set; }

  /// <summary>
  /// Client platform ("android" / "ios" / "web"). Lets the backend attach the right
  /// OAuth params to the Plaid link token (e.g. android_package_name on Android).
  /// </summary>
  public string? Platform { get; set; }
}

/// <summary>Request body for POST /plaid/create-link-token (all fields optional).</summary>
public class CreateLinkTokenBody
{
  public string? Platform { get; set; }
}

public class CreateLinkTokenResponse
{
  public string LinkToken { get; set; } = string.Empty;
}

public class ExchangePublicTokenRequest
{
  public string PublicToken { get; set; } = string.Empty;
  public int UserId { get; set; }
}

public class ExchangePublicTokenResponse
{
  // NOTE: the plaintext Plaid access_token is NEVER returned to the client. It is a
  // server-only secret (full read access to the user's bank data) and is persisted
  // encrypted in PlaidConnection.AccessToken, re-decrypted server-side when needed.
  // The Plaid Link client flow only ever needs the item id.
  public string ItemId { get; set; } = string.Empty;
}

public class AccountDto
{
  public int Id { get; set; }
  public string PlaidAccountId { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public string OfficialName { get; set; } = string.Empty;
  public string? Nickname { get; set; } // User-defined custom name for the account
  public string? Mask { get; set; } // Last 4 digits of account number
  public AccountType Type { get; set; }
  public string Subtype { get; set; } = string.Empty;
  public decimal CurrentBalance { get; set; }
  public decimal AvailableBalance { get; set; }
  public string Currency { get; set; } = string.Empty;
  public bool IsActive { get; set; }
  
  // Family sharing info - shows who owns the account when viewing as family member
  public int? OwnerUserId { get; set; }
  public string? OwnerName { get; set; }

  // Whose budget this account's spend feeds by default (null = the family budget).
  public int? AssociatedUserId { get; set; }
}

public class AccountConnectionDto
{
  public int Id { get; set; }
  public string InstitutionName { get; set; } = string.Empty;
  public string InstitutionId { get; set; } = string.Empty;
  /// <summary>Which provider the connection came from ("Plaid", "SimpleFin"). Reconnecting differs by provider.</summary>
  public string Provider { get; set; } = "Plaid";
  public DateTime LastSyncAt { get; set; }
  public bool IsActive { get; set; }
  /// <summary>The bank needs the user to sign in again (Plaid ITEM_LOGIN_REQUIRED or
  /// PENDING_EXPIRATION). The connection stays active, so the app keys "needs attention" off this.</summary>
  public bool RequiresUserAction { get; set; }
  public List<AccountDto> Accounts { get; set; } = new List<AccountDto>();
  public int TotalTransactions { get; set; }
  
  // Owner info for family sharing
  public int? OwnerUserId { get; set; }
  public string? OwnerName { get; set; }
}

public class AccountConnectionStatusDto
{
  public int Id { get; set; }
  public bool IsActive { get; set; }
  public DateTime LastSyncAt { get; set; }
  public string Status { get; set; } = string.Empty;
  public string? ErrorMessage { get; set; }
}

public class ImportProgressDto
{
  public int ConnectionId { get; set; }
  public string Status { get; set; } = string.Empty;
  public int Progress { get; set; } // 0-100 percentage
  public string? Message { get; set; }
  public DateTime? StartedAt { get; set; }
  public DateTime? CompletedAt { get; set; }
  public DateTime? QuickSyncCompletedAt { get; set; }
  public bool CanDismiss { get; set; } // Whether user can dismiss the notification
}

public class UpdateAccountNicknameRequest
{
  public string? Nickname { get; set; } // Null or empty to clear the nickname
}

public class UpdateAccountNicknameResponse
{
  public int AccountId { get; set; }
  public string? Nickname { get; set; }
  public string DisplayName { get; set; } = string.Empty; // The resolved display name (nickname > officialName > name)
}

public class UpdateAccountAssociationRequest
{
  // The family member whose budget this account's spend feeds by default. Null = the family budget.
  public int? AssociatedUserId { get; set; }
}

public class AccountAssociationResponse
{
  public int AccountId { get; set; }
  public int? AssociatedUserId { get; set; }
}
