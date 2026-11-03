# Localization (gen_l10n)

English (`app_en.arb`) is the template; every user-facing string added to the
app goes here first, never as a hard-coded literal (the ratchet test
`test/lint/hardcoded_string_ratchet_test.dart` enforces this). The app is
English only (owner decision 2026-10-12: a solo founder can't support other
languages). Spanish was removed; the ARB setup is kept so strings stay out of
widgets and a locale can be added later.

- Adding a locale = adding one `app_XX.arb` file (parked; see docs/international_readiness.md). Keys missing from a locale
  fall back to English at generation time, so partial translations ship safely.
- Generated code lands in `lib/l10n/generated/` (gitignored; produced by
  `flutter pub get` / any build via `l10n.yaml`).
- Usage: `AppLocalizations.of(context).keyName`. Amounts NEVER go through ARB
  strings — currency formatting lives in `utils/formatters.dart` (#941).
- Externalization state: login page + the #941 strings are done; remaining
  pages are swept incrementally (see issue #941). New pages must start
  localized.

## Checks

- `test/lint/arb_consistency_test.dart` checks any non-English ARB file that exists (none today): orphan keys, placeholders, empties, and untranslated English. Same-as-English exemptions are per language (a new locale starts with only the brand/badge ones).
- `scripts/gen_pseudo_l10n.py` writes a pseudo-locale (`en-XA`) to find hard-coded strings and clipped layouts.
