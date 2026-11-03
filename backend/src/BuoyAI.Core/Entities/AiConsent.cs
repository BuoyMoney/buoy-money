using System.Linq.Expressions;

namespace BuoyAI.Core.Entities;

/// <summary>
/// The one rule for "may this user's data be sent to Google Gemini", shared by the request
/// gate (RequiresAiConsentAttribute) and every background path (categorization drainer,
/// insight queue, Wrapped garnish, subscription enhancement, embeddings, MCP chat search).
/// They used to check it four slightly different ways.
///
/// App Store 5.1.1(i) / 5.1.2(i): personal data may go to a third-party AI only after the app
/// says what is sent and to whom and the user explicitly agrees. Consent is explicit on every
/// platform; there is no implied grant.
/// </summary>
public static class AiConsent
{
  /// <summary>
  /// Bump whenever the consent sheet starts disclosing something new. Grants recorded under an
  /// older version stop counting, so everyone is asked again on the new sheet.
  ///
  /// 1 (implicit, null in the DB): grants before versioning. Some were implied on Android/web
  ///   without the user seeing anything, and the sheet listed only part of what is sent.
  /// 2 (2026-09-26): explicit opt-in everywhere; the sheet names Google Gemini and every data
  ///   category. Written after App Review rejected the iOS build under 5.1.1(i)/5.1.2(i).
  /// </summary>
  public const int CurrentVersion = 2;

  /// <summary>Translatable to SQL, for queries.</summary>
  public static readonly Expression<Func<User, bool>> IsCurrentExpr = u =>
    u.AiConsentGrantedAt != null
    && (u.AiConsentWithdrawnAt == null || u.AiConsentWithdrawnAt < u.AiConsentGrantedAt)
    && u.AiConsentVersion != null
    && u.AiConsentVersion >= CurrentVersion;

  private static readonly Func<User, bool> IsCurrentFn = IsCurrentExpr.Compile();

  /// <summary>True when the user explicitly agreed to the current disclosure and has not withdrawn.</summary>
  public static bool IsCurrent(User? user) => user != null && IsCurrentFn(user);

  /// <summary>
  /// The grant time to show clients: null unless the grant is current. Older app builds read
  /// "granted" from this field, so masking a stale grant here is what makes them ask again.
  /// </summary>
  public static DateTime? VisibleGrantedAt(User user) => IsCurrent(user) ? user.AiConsentGrantedAt : null;
}
