#!/usr/bin/env python3
"""The feed side of a release with a delta package, for release-mac.sh and the Windows workflow.

A delta package turns the previous full package into the new one, so an installed copy downloads the
files that changed instead of the whole app. Three steps need the same rules on both platforms, so
they are here once:

  previous   Read the live feed, refuse a version that is not newer than everything in it, download
             the newest full package and check its SHA-256. Prints that package's path.
  same-files Compare two packages file by file (name, size, CRC-32). A delta is published only when
             the package that Velopack's own updater rebuilds from it holds the same files as the new
             full package. The two zips differ as files, because the rebuild is compressed again and
             keeps no file modes.
  feed       Write releases.<channel>.json and RELEASES-<channel>: every asset of the previous feed,
             unchanged, plus the new full package and its delta.

The rules that an installed copy relies on (RPM.Desktop/Services/Updates/DeltaChain.cs):
  * a delta is made from the newest full package in the feed, which is what `previous` returns;
  * the feed only grows: `feed` keeps every earlier asset, so the full release that a delta was made
    from is always listed just before it.

Standard library only. Run as `python3 -I scripts/release_delta.py ...`.
"""

import argparse
import hashlib
import json
import os
import re
import sys
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

USER_AGENT = "rpm-release"


def fail(message):
    print(f"FATAL: {message}", file=sys.stderr)
    sys.exit(1)


# ── Versions ─────────────────────────────────────────────────────────────────

_VERSION = re.compile(r"^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$")


def version_key(text):
    """Returns a sort key that orders versions as SemVer 2 (and Velopack) does."""
    m = _VERSION.match(text.strip())
    if not m:
        raise ValueError(f"not a version: {text!r}")
    major, minor, patch, pre = m.groups()
    core = (int(major), int(minor), int(patch))
    if pre is None:
        # A release sorts after every pre-release of the same core.
        return core + ((1,),)
    parts = tuple((0, int(p), "") if p.isdigit() else (1, 0, p) for p in pre.split("."))
    return core + ((0,) + parts,)


# ── Packages ─────────────────────────────────────────────────────────────────

def file_hashes(path):
    sha1, sha256 = hashlib.sha1(), hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            sha1.update(block)
            sha256.update(block)
    return sha1.hexdigest().upper(), sha256.hexdigest().upper()


def package_identity(path):
    """Returns the id and version from the package's own .nuspec, which is what Velopack reads."""
    with zipfile.ZipFile(path) as z:
        specs = [n for n in z.namelist() if n.endswith(".nuspec") and "/" not in n]
        if len(specs) != 1:
            fail(f"{path} has {len(specs)} .nuspec files at its root, not one")
        root = ET.fromstring(z.read(specs[0]))
    values = {el.tag.rsplit("}", 1)[-1]: (el.text or "").strip() for el in root.iter()}
    if not values.get("id") or not values.get("version"):
        fail(f"{path}: the .nuspec names no id or version")
    return values["id"], values["version"]


def asset_for(path, kind):
    package_id, version = package_identity(path)
    sha1, sha256 = file_hashes(path)
    return {
        "PackageId": package_id,
        "Version": version,
        "Type": kind,
        "FileName": os.path.basename(path),
        "SHA1": sha1,
        "SHA256": sha256,
        "Size": os.path.getsize(path),
    }


def package_files(path):
    """Returns every file in a package as name -> (size, CRC-32). Folders have nothing to compare."""
    with zipfile.ZipFile(path) as z:
        return {i.filename: (i.file_size, i.CRC) for i in z.infolist() if not i.is_dir()}


def different_files(a, b):
    """Returns the sorted names that are in only one package or that differ between them."""
    fa, fb = package_files(a), package_files(b)
    return sorted(n for n in set(fa) | set(fb) if fa.get(n) != fb.get(n))


# ── The feed ─────────────────────────────────────────────────────────────────

def feed_name(channel):
    return f"releases.{channel}.json"


def read_feed(url):
    """Returns the assets of a live feed. If the feed cannot be read, the release stops: publishing
    without it would drop every earlier entry from the feed."""
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT, "Cache-Control": "no-cache"})
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            data = json.load(response)
    except urllib.error.HTTPError as e:
        fail(f"{url} answered {e.code}. If this channel has never been published, pass --first-release.")
    except (urllib.error.URLError, OSError, ValueError) as e:
        fail(f"could not read {url}: {e}")
    assets = data.get("Assets")
    if not isinstance(assets, list):
        fail(f"{url} has no Assets list")
    return assets


def newer_than_all(version, assets):
    newest = max((a["Version"] for a in assets), key=version_key, default=None)
    if newest is not None and version_key(version) <= version_key(newest):
        fail(f"{version} is not newer than {newest}, which the feed already has. Installed copies "
             f"would not take it, and a delta from {newest} to it would run backwards.")


def download(url, target, sha256, size):
    """Downloads to target and checks the SHA-256 and the size that the feed gives. A file that does
    not match is deleted."""
    partial = target + ".partial"
    digest = hashlib.sha256()
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    try:
        with urllib.request.urlopen(request, timeout=600) as response, open(partial, "wb") as out:
            for block in iter(lambda: response.read(1 << 20), b""):
                digest.update(block)
                out.write(block)
    except (urllib.error.URLError, OSError) as e:
        if os.path.exists(partial):
            os.remove(partial)
        fail(f"could not download {url}: {e}")
    got_size = os.path.getsize(partial)
    if digest.hexdigest().upper() != sha256.upper() or got_size != size:
        os.remove(partial)
        fail(f"{url} is not the package the feed names (SHA-256 {digest.hexdigest().upper()}, "
             f"{got_size} bytes; the feed says {sha256.upper()}, {size} bytes)")
    os.replace(partial, target)


def write_feed(assets, channel, out_dir):
    ordered = sorted(assets, key=lambda a: (version_key(a["Version"]), a["Type"] != "Delta"), reverse=True)
    with open(os.path.join(out_dir, feed_name(channel)), "w", encoding="utf-8") as f:
        json.dump({"Assets": ordered}, f, separators=(",", ":"))
    # The legacy file, in the format that vpk writes: full packages only, one per line.
    with open(os.path.join(out_dir, f"RELEASES-{channel}"), "w", encoding="utf-8", newline="\n") as f:
        for a in ordered:
            if a["Type"] == "Full":
                f.write(f"{a['SHA1']} {a['FileName']} {a['Size']}\n")


# ── Commands ─────────────────────────────────────────────────────────────────

def cmd_previous(args):
    os.makedirs(args.out, exist_ok=True)
    kept = os.path.join(args.out, feed_name(args.channel))
    if args.first_release:
        with open(kept, "w", encoding="utf-8") as f:
            json.dump({"Assets": []}, f)
        return
    base = args.feed if args.feed.endswith("/") else args.feed + "/"
    assets = read_feed(base + feed_name(args.channel))
    newer_than_all(args.version, assets)
    if args.no_delta:
        # The feed is kept, so the release adds to it. No package is fetched, because no delta is made.
        with open(kept, "w", encoding="utf-8") as f:
            json.dump({"Assets": assets}, f)
        return
    fulls = [a for a in assets if a.get("Type") == "Full"]
    if not fulls:
        fail(f"the {args.channel} feed lists no full package to make a delta from")
    latest = max(fulls, key=lambda a: version_key(a["Version"]))
    target = os.path.join(args.out, latest["FileName"])
    download(base + latest["FileName"], target, latest["SHA256"], latest["Size"])
    with open(kept, "w", encoding="utf-8") as f:
        json.dump({"Assets": assets}, f)
    print(target)


def cmd_same_files(args):
    different = different_files(args.expected, args.rebuilt)
    if different:
        print(f"FATAL: {args.rebuilt} does not hold the files of {args.expected}:", file=sys.stderr)
        for name in different[:40]:
            print(f"  {name}", file=sys.stderr)
        sys.exit(1)
    print(f"same files: {len(package_files(args.expected))}")


def cmd_feed(args):
    with open(args.previous, encoding="utf-8") as f:
        previous = json.load(f)["Assets"]
    full = asset_for(args.full, "Full")
    newer_than_all(full["Version"], previous)
    assets = list(previous) + [full]
    if args.delta:
        delta = asset_for(args.delta, "Delta")
        if (delta["PackageId"], delta["Version"]) != (full["PackageId"], full["Version"]):
            fail(f"{args.delta} is {delta['PackageId']} {delta['Version']}, not {full['PackageId']} {full['Version']}")
        assets.append(delta)
    write_feed(assets, args.channel, args.out)
    print(f"{feed_name(args.channel)}: {len(assets)} assets, newest {full['Version']}")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("previous", help="download the newest full package of the live feed")
    p.add_argument("--feed", required=True, help="the channel's folder on the update host, e.g. https://updates.rpmrpmrpm.com/win-x64/")
    p.add_argument("--channel", required=True)
    p.add_argument("--version", required=True, help="the version being released")
    p.add_argument("--out", required=True, help="where the package and a copy of the feed go")
    first = p.add_mutually_exclusive_group()
    first.add_argument("--first-release", action="store_true", help="the channel has never been published")
    first.add_argument("--no-delta", action="store_true",
                       help="keep the live feed but fetch nothing: this release ships as a full package only")
    p.set_defaults(run=cmd_previous)

    p = sub.add_parser("same-files", help="fail unless two packages hold the same files")
    p.add_argument("expected")
    p.add_argument("rebuilt")
    p.set_defaults(run=cmd_same_files)

    p = sub.add_parser("feed", help="write the feed: the previous one plus this release")
    p.add_argument("--previous", required=True, help="the feed copy `previous` wrote")
    p.add_argument("--channel", required=True)
    p.add_argument("--full", required=True)
    # One of the two is always required: a release without a delta must be chosen on purpose.
    group = p.add_mutually_exclusive_group(required=True)
    group.add_argument("--delta", help="the delta package made from the previous full package")
    group.add_argument("--no-delta", action="store_true", help="publish this version as a full download only")
    p.add_argument("--out", required=True)
    p.set_defaults(run=cmd_feed)

    args = parser.parse_args(argv)
    args.run(args)


if __name__ == "__main__":
    main()
