#!/usr/bin/env python3
"""Generate a pseudo-locale (en-XA) from the English ARB (#941).

Pseudo-localization is how a non-multilingual owner verifies l10n visually:
every localized string becomes accented and ~40% longer, wrapped in brackets,
so with the device set to the en-XA pseudolocale you can see at a glance:

  - text that ISN'T bracketed  -> a hard-coded string the sweep missed
  - text that overflows/clips  -> a layout that will break in German/French
  - text that reads normally   -> not going through AppLocalizations at all

Usage:
    python scripts/gen_pseudo_l10n.py     # writes lib/l10n/app_en_XA.arb
    flutter run                            # then set device locale to en-XA
                                           # (Android: Settings > System >
                                           #  Developer options > Force
                                           #  pseudolocale, or add en-XA as a
                                           #  language; emulator supports it)

The generated file is gitignored — it is a dev tool, not a shipping locale.
Placeholders ({name}) and ICU plural/select syntax are preserved untouched.
"""
import json
import re
from pathlib import Path

ACCENTS = str.maketrans(
    "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ",
    "àƀçđéƒĝĥìĵķłɱñòƥɋŕšŧùṽŵẋýžÀƁÇĐÉƑĜĤÌĴĶŁϺÑÒƤɊŔŠŦÙṼŴẊÝŽ",
)

# Split out anything inside {...} (placeholders and ICU argument syntax) so it
# survives verbatim; only the literal text between them is pseudo-ized.
TOKEN = re.compile(r"(\{[^{}]*\}|\{|\})")


def pseudoize(message: str) -> str:
    # ICU plural/select arguments nest braces in ways the simple tokenizer
    # can't safely accent (the argument name and keywords must stay ASCII or
    # gen_l10n rejects the file). Those messages keep their original text and
    # rely on the brackets + padding for the visual signal.
    if re.search(r",\s*(plural|select)\s*,", message):
        pad = "~" * max(2, int(len(message) * 0.4))
        return f"[{message}{pad}]"

    parts = TOKEN.split(message)
    out = []
    for part in parts:
        if part.startswith("{") or part in ("{", "}"):
            out.append(part)
        else:
            accented = part.translate(ACCENTS)
            out.append(accented)
    text = "".join(out)
    # ~40% expansion approximates German/French growth; padding goes OUTSIDE
    # the brackets' content so truncation is visible at either end.
    pad = "~" * max(2, int(len(message) * 0.4))
    return f"[{text}{pad}]"


def main() -> None:
    l10n_dir = Path(__file__).resolve().parent.parent / "lib" / "l10n"
    template = json.loads((l10n_dir / "app_en.arb").read_text(encoding="utf-8"))

    pseudo = {"@@locale": "en_XA"}
    for key, value in template.items():
        if key.startswith("@"):
            continue
        pseudo[key] = pseudoize(value)

    out = l10n_dir / "app_en_XA.arb"
    out.write_text(json.dumps(pseudo, ensure_ascii=False, indent=2) + "\n",
                   encoding="utf-8")
    print(f"wrote {out} ({len(pseudo) - 1} messages)")
    print("run `flutter pub get` to regenerate, then set the device locale to en-XA")


if __name__ == "__main__":
    main()
