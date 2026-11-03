/// The product's display name and the few public details that go with it, in one place so a
/// rename is a one-line change.
///
/// Display copy only. Package ids, bundle ids, storage keys, the hosts (`buoy-ai.com`), OAuth scopes
/// and the passkey relying-party id are identifiers and keep their old spelling on purpose: changing
/// them would sign people out, drop saved passkeys or orphan stored data.
class AppBrand {
  AppBrand._();

  /// What people see: app label, lock screen, share text, support mail.
  static const String name = 'Buoy Money';

  /// The short form the marketing site uses in running text.
  static const String shortName = 'Buoy';

  /// The company that operates the hosted service. Legal text and the copyright line name it.
  static const String legalEntity = 'BuoyAI LLC';

  /// The paid hosted add-ons (bank feed and AI). A product name, not the app's name.
  static const String cloudName = 'BuoyAI Cloud';

  /// Where the hosted service lives. Unchanged by the rename.
  static const String siteHost = 'buoy-ai.com';

  static const String supportEmail = 'support@buoy-ai.com';
}
