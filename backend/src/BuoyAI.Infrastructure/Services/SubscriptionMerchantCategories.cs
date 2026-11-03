using System.Text.RegularExpressions;
using BuoyAI.Core.Enums;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Guesses a subscription category from a merchant name. Keywords match whole words only, so "ATT"
/// does not fire on "MATTRESS FIRM" and "GAS" does not fire on "VEGAS". Categories are checked in
/// table order and the first match wins.
/// </summary>
public static class SubscriptionMerchantCategories
{
  private static readonly (SubscriptionCategory Category, string[] Keywords)[] Table =
  [
    (SubscriptionCategory.Streaming, [
      "NETFLIX", "HULU", "SPOTIFY", "APPLE MUSIC", "YOUTUBE", "DISNEY", "HBO", "MAX", "PEACOCK", "PARAMOUNT",
      "PRIME VIDEO", "AMAZON PRIME", "APPLE TV", "TIDAL", "PANDORA", "DEEZER", "AUDIBLE", "SIRIUS",
      "CRUNCHYROLL", "FUNIMATION"]),
    (SubscriptionCategory.Software, [
      "MICROSOFT", "ADOBE", "OFFICE", "GOOGLE", "DROPBOX", "SLACK", "ZOOM", "NOTION", "GITHUB", "ATLASSIAN",
      "FIGMA", "CANVA", "GRAMMARLY", "LASTPASS", "1PASSWORD", "NORDVPN", "EXPRESSVPN", "DASHLANE",
      "EVERNOTE", "TODOIST", "OPENAI", "CHATGPT", "MIDJOURNEY", "CLAUDE"]),
    (SubscriptionCategory.Utilities, [
      "ELECTRIC", "GAS", "WATER", "INTERNET", "PHONE", "WIRELESS", "VERIZON", "T-MOBILE", "AT&T", "ATT",
      "COMCAST", "XFINITY", "SPECTRUM", "COX", "CENTERPOINT", "DUKE ENERGY", "PG&E", "SCE", "CONEDISON",
      "NATIONAL GRID"]),
    (SubscriptionCategory.Fitness, [
      "GYM", "FITNESS", "YOGA", "PELOTON", "PLANET", "EQUINOX", "24 HOUR", "LA FITNESS", "ORANGETHEORY",
      "CROSSFIT", "GOLD'S", "ANYTIME", "CLASSPASS", "MINDBODY", "STRAVA", "CALM", "HEADSPACE", "NOOM",
      "MYFITNESSPAL", "WEIGHT WATCHERS"]),
    (SubscriptionCategory.News, [
      "NEW YORK TIMES", "NYT", "WASHINGTON POST", "WSJ", "WALL STREET JOURNAL", "ECONOMIST", "MEDIUM",
      "SUBSTACK", "PATREON", "NEWS"]),
    (SubscriptionCategory.Gaming, [
      "XBOX", "PLAYSTATION", "NINTENDO", "STEAM", "TWITCH", "EA PLAY", "GAME PASS", "EPIC GAMES", "DISCORD",
      "BLIZZARD"]),
    (SubscriptionCategory.Shopping, [
      "DOORDASH", "UBER EATS", "GRUBHUB", "INSTACART", "HELLOFRESH", "BLUE APRON", "FRESHLY", "FACTOR",
      "HOME CHEF", "SUNBASKET"]),
    (SubscriptionCategory.Shopping, [
      "AMAZON", "COSTCO", "SAM'S CLUB", "BJ'S", "WALMART+", "TARGET CIRCLE", "STITCH FIX", "RENT THE RUNWAY"]),
    (SubscriptionCategory.Insurance, [
      "INSURANCE", "GEICO", "STATE FARM", "PROGRESSIVE", "ALLSTATE", "LIBERTY MUTUAL", "USAA", "FARMERS"]),
  ];

  private static readonly (SubscriptionCategory Category, Regex Pattern)[] Patterns = Table
    .Select(row => (row.Category, new Regex(
      "(?<![A-Z0-9])(?:" + string.Join("|", row.Keywords.Select(Regex.Escape)) + ")(?![A-Z0-9])",
      RegexOptions.Compiled | RegexOptions.CultureInvariant)))
    .ToArray();

  public static SubscriptionCategory Guess(string merchantName)
  {
    var name = merchantName.ToUpperInvariant();
    foreach (var (category, pattern) in Patterns)
    {
      if (pattern.IsMatch(name)) return category;
    }
    return SubscriptionCategory.Other;
  }
}
