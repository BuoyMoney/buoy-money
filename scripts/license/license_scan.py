#!/usr/bin/env python3
"""Dependency license scan for .NET, Dart (pub) and npm.

Standard library only, so the CI job installs nothing third-party.

    python scripts/license/license_scan.py --ecosystem dotnet|dart|npm|all [--enforce]

Prerequisites (the workflow does these): `dotnet restore backend/BuoyAI.sln`,
`flutter pub get` in flutter/, `npm ci` in marketing/.

Report-only by default: always exits 0 and prints a markdown report (also appended to
$GITHUB_STEP_SUMMARY). With --enforce it exits 1 if any package lands in the deny
bucket or has no recognisable license. Policy lives in scripts/license/policy.json.
"""
import argparse
import glob
import json
import os
import re
import sys
import xml.etree.ElementTree as ET
from urllib.parse import unquote, urlparse

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

# licenseUrl values that older NuGet packages use instead of an SPDX expression.
URL_HINTS = [
    (r"apache\.org/licenses/LICENSE-2\.0", "Apache-2.0"),
    (r"opensource\.org/licenses/MIT|licenses/mit", "MIT"),
    (r"opensource\.org/licenses/BSD-3", "BSD-3-Clause"),
    (r"opensource\.org/licenses/BSD-2", "BSD-2-Clause"),
    (r"opensource\.org/licenses/MS-PL", "MS-PL"),
    (r"opensource\.org/licenses/ISC", "ISC"),
    (r"go\.microsoft\.com/fwlink/\?LinkId=329770", "Proprietary"),
]

# Text heuristics for LICENSE files (Dart packages, NuGet file licenses). Order matters.
TEXT_HINTS = [
    (r"GNU AFFERO GENERAL PUBLIC LICENSE", "AGPL-3.0-only"),
    (r"GNU LESSER GENERAL PUBLIC LICENSE Version 3", "LGPL-3.0-only"),
    (r"GNU LESSER GENERAL PUBLIC LICENSE|GNU LIBRARY GENERAL PUBLIC LICENSE", "LGPL-2.1-only"),
    (r"GNU GENERAL PUBLIC LICENSE Version 3", "GPL-3.0-only"),
    (r"GNU GENERAL PUBLIC LICENSE Version 2", "GPL-2.0-only"),
    (r"Server Side Public License", "SSPL-1.0"),
    (r"Business Source License", "BUSL-1.1"),
    (r"Mozilla Public License,? (Version|v\.?) ?2\.0", "MPL-2.0"),
    (r"Apache License,? Version 2\.0", "Apache-2.0"),
    (r"Permission is hereby granted, free of charge", "MIT"),
    (r"Permission to use, copy, modify, and/or distribute this software for any purpose", "ISC"),
    (r"Permission to use, copy, modify, and distribute this software for any purpose", "ISC"),
    (r"Redistribution and use in source and binary forms.*?Neither the name", "BSD-3-Clause"),
    (r"Redistribution and use in source and binary forms", "BSD-2-Clause"),
    (r"free and unencumbered software released into the public domain", "Unlicense"),
    (r"Boost Software License", "BSL-1.0"),
    (r"Creative Commons Zero|CC0 1\.0", "CC0-1.0"),
    (r"provided 'as-is', without any express or implied warranty", "Zlib"),
]


def load_policy():
    with open(os.path.join(os.path.dirname(__file__), "policy.json"), encoding="utf-8") as f:
        return json.load(f)


def classify_text(text):
    flat = re.sub(r"\s+", " ", text)
    for pat, spdx in TEXT_HINTS:
        if re.search(pat, flat, re.I | re.S):
            return spdx
    return None


def read_license_file(directory):
    if not os.path.isdir(directory):
        return None
    for name in sorted(os.listdir(directory)):
        full = os.path.join(directory, name)
        if re.match(r"(licen[sc]e|copying|unlicense)", name, re.I) and os.path.isfile(full):
            with open(full, encoding="utf-8", errors="replace") as f:
                return classify_text(f.read(20000))
    return None


# ---------------------------------------------------------------- .NET
def nuspec_license(nuspec, pdir):
    root = ET.parse(nuspec).getroot()
    meta = next((el for el in root.iter() if el.tag.endswith("metadata")), None)
    if meta is None:
        return None, "no metadata"
    lic = url = None
    for el in meta:
        tag = el.tag.split("}")[-1]
        if tag == "license":
            lic = el
        elif tag == "licenseUrl":
            url = (el.text or "").strip()
    if lic is not None and lic.get("type") == "expression":
        return (lic.text or "").strip(), "nuspec expression"
    if lic is not None and lic.get("type") == "file":
        fp = os.path.join(pdir, (lic.text or "").strip())
        if os.path.isfile(fp):
            with open(fp, encoding="utf-8", errors="replace") as f:
                return classify_text(f.read(20000)), "nuspec license file"
    if url:
        for pat, spdx in URL_HINTS:
            if re.search(pat, url, re.I):
                return spdx, "licenseUrl"
        return None, "licenseUrl " + url
    return None, "none"


def scan_dotnet():
    found = {}
    assets = glob.glob(os.path.join(ROOT, "backend", "**", "obj", "project.assets.json"), recursive=True)
    if not assets:
        print("warning: no project.assets.json under backend/; run `dotnet restore` first", file=sys.stderr)
    for path in assets:
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
        folders = list(data.get("packageFolders", {}).keys())
        for key, lib in data.get("libraries", {}).items():
            if lib.get("type") != "package":
                continue
            name, version = key.split("/", 1)
            if (name, version) in found:
                continue
            spdx, how = None, "no nuspec found"
            for folder in folders:
                pdir = os.path.join(folder, lib["path"])
                nuspec = os.path.join(pdir, name.lower() + ".nuspec")
                if os.path.isfile(nuspec):
                    spdx, how = nuspec_license(nuspec, pdir)
                    break
            found[(name, version)] = (spdx, how)
    return [("dotnet", n, v, s, h) for (n, v), (s, h) in sorted(found.items())]


# ---------------------------------------------------------------- Dart
def scan_dart():
    cfg = os.path.join(ROOT, "flutter", ".dart_tool", "package_config.json")
    if not os.path.isfile(cfg):
        print("warning: flutter/.dart_tool/package_config.json missing; run `flutter pub get`", file=sys.stderr)
        return []
    with open(cfg, encoding="utf-8") as f:
        data = json.load(f)
    base = os.path.dirname(cfg)
    out = []
    for pkg in sorted(data.get("packages", []), key=lambda p: p["name"]):
        uri = pkg["rootUri"]
        if uri.startswith("file://"):
            root = unquote(urlparse(uri).path)
            if re.match(r"^/[A-Za-z]:", root):
                root = root[1:]
        else:
            root = os.path.normpath(os.path.join(base, uri))
        norm = root.replace("\\", "/")
        if os.path.normpath(root) == os.path.normpath(os.path.join(base, "..")):
            continue  # the app itself
        if "/bin/cache/pkg/" in norm or "/flutter/packages/" in norm:
            out.append(("dart", pkg["name"], "sdk", "BSD-3-Clause", "Flutter SDK"))
            continue
        ver = os.path.basename(norm.rstrip("/")).rsplit("-", 1)[-1]
        out.append(("dart", pkg["name"], ver, read_license_file(root), "LICENSE file"))
    return out


# ---------------------------------------------------------------- npm
NPM_PROJECTS = ["marketing", os.path.join("scripts", "media", "motion")]


def scan_npm():
    out = []
    for proj in NPM_PROJECTS:
        out += scan_npm_project(proj)
    return out


def scan_npm_project(proj):
    lock_path = os.path.join(ROOT, proj, "package-lock.json")
    if not os.path.isfile(lock_path):
        return []
    with open(lock_path, encoding="utf-8") as f:
        lock = json.load(f)
    out = []
    for path, meta in sorted(lock.get("packages", {}).items()):
        if not path:
            continue
        name = path.split("node_modules/")[-1]
        spdx, how = meta.get("license"), "lockfile"
        if isinstance(spdx, dict):
            spdx = spdx.get("type")
        if isinstance(spdx, list):
            spdx = " OR ".join(x.get("type", str(x)) if isinstance(x, dict) else str(x) for x in spdx)
        if not spdx:
            pj = os.path.join(ROOT, proj, path, "package.json")
            if os.path.isfile(pj):
                with open(pj, encoding="utf-8") as f:
                    d = json.load(f)
                spdx = d.get("license")
                if isinstance(spdx, dict):
                    spdx = spdx.get("type")
                how = "node_modules package.json"
                if not spdx:
                    spdx = read_license_file(os.path.dirname(pj))
                    how = "LICENSE file"
        out.append(("npm", name, meta.get("version", ""), spdx, how + " (" + proj.replace(os.sep, "/") + ")"))
    return out


# ---------------------------------------------------------------- policy
def bucket_of(token, policy):
    token = token.strip().strip("()").strip()
    token = re.sub(r"\s+WITH\s+.*$", "", token, flags=re.I)
    if token.endswith("+"):
        token = token[:-1] + "-or-later"
    if token in policy["deny"] or re.search(r"(^|-)NC(-|$)", token):
        return 2
    if token in policy["review"]:
        return 1
    if token in policy["allow"]:
        return 0
    return 3


def bucket_expr(expr, policy):
    """0 allow, 1 review, 2 deny, 3 unknown.

    Parses SPDX expressions with parentheses. AND binds tighter than OR; OR takes the
    best alternative, AND the worst. A malformed expression is classed unknown.
    """
    tokens = re.findall(r"\(|\)|[^\s()]+", expr)
    pos = 0

    def peek():
        return tokens[pos] if pos < len(tokens) else None

    def parse_or():
        nonlocal pos
        vals = [parse_and()]
        while peek() is not None and peek().upper() == "OR":
            pos += 1
            vals.append(parse_and())
        return min(vals)

    def parse_and():
        nonlocal pos
        vals = [parse_atom()]
        while peek() is not None and peek().upper() == "AND":
            pos += 1
            vals.append(parse_atom())
        return max(vals)

    def parse_atom():
        nonlocal pos
        t = peek()
        if t is None:
            raise ValueError("unexpected end")
        if t == "(":
            pos += 1
            v = parse_or()
            if peek() != ")":
                raise ValueError("missing )")
            pos += 1
            return v
        if t == ")" or t.upper() in ("AND", "OR", "WITH"):
            raise ValueError("unexpected " + t)
        pos += 1
        name = t
        if peek() is not None and peek().upper() == "WITH":
            pos += 1
            if peek() is None:
                raise ValueError("missing exception")
            pos += 1
        return bucket_of(name, policy)

    try:
        v = parse_or()
        if pos != len(tokens):
            raise ValueError("trailing tokens")
        return v
    except ValueError:
        return 3


NAMES = ["allow", "review", "DENY", "UNKNOWN"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ecosystem", choices=["dotnet", "dart", "npm", "all"], default="all")
    ap.add_argument("--enforce", action="store_true", help="exit 1 on deny or unknown licenses")
    args = ap.parse_args()
    policy = load_policy()
    overrides = {k: v for k, v in policy.get("package_overrides", {}).items() if not k.startswith("_")}

    rows = []
    if args.ecosystem in ("dotnet", "all"):
        rows += scan_dotnet()
    if args.ecosystem in ("dart", "all"):
        rows += scan_dart()
    if args.ecosystem in ("npm", "all"):
        rows += scan_npm()

    counts = [0, 0, 0, 0]
    flagged = []
    per_license = {}
    for eco, name, ver, spdx, how in rows:
        spdx = overrides.get(f"{eco}:{name}", spdx)
        b = 3 if not spdx else bucket_expr(spdx, policy)
        counts[b] += 1
        label = spdx or "UNKNOWN"
        per_license[label] = per_license.get(label, 0) + 1
        if b:
            flagged.append((b, eco, name, ver, label, how))

    lines = [f"## License scan ({args.ecosystem})", ""]
    lines.append(
        f"{len(rows)} packages: {counts[0]} allowed, {counts[1]} need review, "
        f"{counts[2]} denied, {counts[3]} unknown."
    )
    lines += ["", "| License | Packages |", "|---|---|"]
    for lic, n in sorted(per_license.items(), key=lambda kv: -kv[1]):
        lines.append(f"| {lic} | {n} |")
    if flagged:
        lines += ["", "### Needs attention", "",
                  "| Status | Ecosystem | Package | Version | License | Source |",
                  "|---|---|---|---|---|---|"]
        for b, eco, name, ver, lic, how in sorted(flagged, key=lambda r: (-r[0], r[1], r[2])):
            lines.append(f"| {NAMES[b]} | {eco} | {name} | {ver} | {lic} | {how} |")
    report = "\n".join(lines)
    print(report)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as f:
            f.write(report + "\n")

    if not rows:
        print("warning: nothing scanned (restore step missing?)", file=sys.stderr)
    if args.enforce and (counts[2] or counts[3]):
        print(f"FAIL: {counts[2]} denied and {counts[3]} unknown licenses.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
