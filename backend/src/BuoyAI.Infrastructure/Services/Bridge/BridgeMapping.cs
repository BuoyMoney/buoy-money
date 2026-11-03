using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Utilities;

namespace BuoyAI.Infrastructure.Services.Bridge;

/// <summary>
/// How a decrypted bridge batch (Plaid's /transactions/sync data, passed through untouched by the Cloud) becomes
/// BuoyAI rows. The Cloud maps nothing; this server owns its own schema.
/// </summary>
public static class BridgeMapping
{
  /// <summary>
  /// Every id from the bridge starts with this. Accounts and transactions of every provider share one unique column,
  /// so the prefix keeps a bridge id from ever equalling a Plaid one (hosted) or a SimpleFIN one.
  /// </summary>
  public const string IdPrefix = "bm:";

  private const int MaxIdLength = 100;

  public static string AccountId(string rawId) => Prefixed(rawId);

  public static string TransactionId(string rawId) => Prefixed(rawId);

  /// <summary>The connection's own id: the Cloud's opaque id for the bank login.</summary>
  public static string ItemId(Guid bridgeItemId) => IdPrefix + bridgeItemId.ToString("N");

  public static bool TryParseItemId(string itemId, out Guid bridgeItemId)
  {
    bridgeItemId = Guid.Empty;
    return itemId.StartsWith(IdPrefix, StringComparison.Ordinal) && Guid.TryParseExact(itemId[IdPrefix.Length..], "N", out bridgeItemId);
  }

  private static string Prefixed(string rawId)
  {
    var id = IdPrefix + rawId.Trim();
    if (id.Length <= MaxIdLength) return id;
    return IdPrefix + "h:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawId)))[..40].ToLowerInvariant();
  }

  public static AccountType MapAccountType(string? type, string? subtype)
  {
    var t = (type ?? "").ToLowerInvariant();
    var s = (subtype ?? "").ToLowerInvariant();
    return t switch
    {
      "credit" => AccountType.CreditCard,
      "depository" when s is "savings" or "cd" or "money market" or "hsa" => AccountType.Savings,
      "depository" => AccountType.Checking,
      "investment" or "brokerage" => AccountType.Investment,
      _ => AccountType.Other
    };
  }

  public static string CurrencyOrDefault(string? currency) =>
    currency is { Length: 3 } && currency.All(char.IsAsciiLetter) ? currency.ToUpperInvariant() : "USD";

  public static IncomingTransaction ToIncoming(JsonNode tx)
  {
    var plaidAmount = Decimal(tx["amount"]);
    var name = Text(tx["name"]);
    var merchant = Text(tx["merchant_name"]);
    var description = !string.IsNullOrWhiteSpace(merchant) ? merchant! : !string.IsNullOrWhiteSpace(name) ? name! : "Transaction";
    var pfc = tx["personal_finance_category"];
    var location = tx["location"];
    var payment = tx["payment_meta"];
    var pendingOf = Text(tx["pending_transaction_id"]);

    return new IncomingTransaction
    {
      Provider = BankProvider.Bridge,
      ExternalTransactionId = Text(tx["transaction_id"]) is { Length: > 0 } id ? TransactionId(id) : string.Empty,
      ExternalAccountId = Text(tx["account_id"]) is { Length: > 0 } acc ? AccountId(acc) : null,
      PendingExternalTransactionId = pendingOf is { Length: > 0 } ? TransactionId(pendingOf) : null,
      IsPending = tx["pending"]?.GetValue<bool>() ?? false,

      // Plaid shows money out as positive; BuoyAI shows money out (an expense) as negative.
      Amount = -plaidAmount,
      Direction = plaidAmount > 0 ? TransactionType.Debit : TransactionType.Credit,

      Date = Date(tx["date"]),
      AuthorizedDate = Date(tx["authorized_date"]),

      Description = SafeText.Truncate(description, 500),
      MerchantName = merchant is { Length: > 0 } ? SafeText.Truncate(merchant, 200) : null,
      OriginalDescription = name is { Length: > 0 } ? SafeText.Truncate(name, 500) : null,
      IsoCurrencyCode = CurrencyOrDefault(Text(tx["iso_currency_code"])),

      // The Cloud passes Plaid's own category through. The ingest pipeline (rules, then the AI queue) decides; the user reviews.
      CategoryId = SystemCategoryIds.Uncategorized,
      NeedsReview = true,
      CategorySource = "Bridge",
      CategoryPrimary = Text(pfc?["primary"]),
      CategoryDetailed = Text(pfc?["detailed"]),
      CategoryConfidence = Text(pfc?["confidence_level"]),
      CategoryIconUrl = Text(tx["personal_finance_category_icon_url"]),

      MerchantEntityId = Text(tx["merchant_entity_id"]),
      MerchantLogoUrl = Text(tx["logo_url"]),
      MerchantWebsite = Text(tx["website"]),
      LocationCity = Text(location?["city"]),
      LocationRegion = Text(location?["region"]),
      LocationStoreNumber = Text(location?["store_number"]),
      PaymentChannel = Text(tx["payment_channel"]),
      CheckNumber = Text(tx["check_number"]),
      TransactionCode = Text(tx["transaction_code"]),
      PaymentPayee = Text(payment?["payee"]),
      PaymentPayer = Text(payment?["payer"]),
      PaymentReason = Text(payment?["reason"]),
      PaymentReferenceNumber = Text(payment?["reference_number"]),
    };
  }

  private static string? Text(JsonNode? node)
  {
    if (node is not JsonValue v) return null;
    return v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
  }

  private static decimal Decimal(JsonNode? node)
  {
    if (node is not JsonValue v) return 0m;
    if (v.TryGetValue<decimal>(out var d)) return d;
    if (v.TryGetValue<double>(out var f)) return (decimal)f;
    return v.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;
  }

  private static DateOnly? Date(JsonNode? node) =>
    Text(node) is { } s && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

  public static decimal? Balance(JsonNode? node)
  {
    if (node is not JsonValue v) return null;
    if (v.TryGetValue<decimal>(out var d)) return d;
    return v.TryGetValue<double>(out var f) ? (decimal)f : null;
  }
}
