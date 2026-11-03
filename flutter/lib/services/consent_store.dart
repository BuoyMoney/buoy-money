/// Cross-surface analytics consent, shared with the Astro marketing site.
///
/// The marketing site (buoy-ai.com) and this app (buoy-ai.com/app/) are the SAME
/// origin, so on web a single first-party cookie lets one consent prompt answer for
/// both. Whichever surface the visitor reaches first is the one that asks; the other
/// reads the answer and stays quiet. See `marketing/public/consent.js` for the other
/// half of the contract — the cookie name and values must match on both sides.
///
/// Off web there is no marketing site to share with, so these are no-ops and
/// SharedPreferences remains the only store.
library;

/// The stored choice: `'granted'`, `'denied'`, or null when never asked.
String? readSharedConsent() => null;

/// Records the choice for every surface on this origin.
void writeSharedConsent(bool granted) {}
