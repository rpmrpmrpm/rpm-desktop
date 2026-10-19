#!/usr/bin/env python3
"""Build the lab's playlists out of the real libraries.

    python3 tools/roomlab/import_library.py --list
    python3 tools/roomlab/import_library.py            # -> library.json + artists/

Two databases are read, and both are COPIED first. They are live files with write
ahead logs beside them and the app may be running; nothing here opens the originals.

  * the peer mirror, for the playlists. A shared library is mirrored whole, so its
    playlists and its tracks are ordinary rows.
  * this machine's own library, for the artist pictures. The peer's copy has art for
    three artists; this one has a thousand pictures over a hundred and thirty-seven,
    because they were fetched here.

**The pictures are the ones the app already fetched, not fresh ones.** The app asks
fanart.tv on demand, keyed by the artist's MusicBrainz id and the user's own API key.
That key is not something a lab should reach for, so what is used here is what that
pipeline has already cached in `artist_art`: the same pictures, chosen by the same
ranker, for the artists that have been played. Artists it has never fetched have no
picture here and are skipped.

**The palette is the app's own function, ported.** `RoomPalette.From` in
RPM.Core/Viz decides the four colours a room is painted in, and it is transcribed
below rather than approximated: the lab is meant to show the colour the app would
calculate, and a second rule that merely resembles it would be showing something else.

What is NOT the app's is where the three colours going INTO it come from. The app
takes them off the records on the list; these come off the artists' pictures, because
the peer's cover files are keyed by a hash this does not know how to reproduce. The
reading is coherent either way, and it is one call to swap once that mapping is known.
"""
import argparse
import hashlib
import json
import os
import math
import pathlib
import shutil
import sqlite3
import sys
import urllib.request

HERE = pathlib.Path(__file__).parent
SUPPORT = pathlib.Path.home() / "Library/Application Support/RPM"
# The library to read: <music folder>/.rpm/library.db, given in RPM_LIBRARY.
OWN = pathlib.Path(os.environ.get("RPM_LIBRARY", ""))
PEERS = SUPPORT / "peers"
CACHE = HERE / "cache"
ART = HERE / "artists"
OUT = HERE / "library.json"


def copy_db(src, into):
    """A database and its write-ahead log, copied before anything reads them."""
    into.parent.mkdir(parents=True, exist_ok=True)
    for suffix in ("", "-wal", "-shm"):
        s = pathlib.Path(str(src) + suffix)
        if s.exists():
            shutil.copy2(s, str(into) + suffix)
    return sqlite3.connect(into)


def peer_db(name_wanted):
    """The mirror of one paired machine, by the name it is paired under."""
    listing = json.loads((SUPPORT / "peers.json").read_text())
    for peer in listing:
        if name_wanted.lower() in peer["name"].lower():
            db = PEERS / peer["deviceId"] / "library/.rpm/library.db"
            if db.exists():
                return peer["name"], copy_db(db, CACHE / "peer.db")
    raise SystemExit(f"no mirrored library for a peer matching {name_wanted!r}")


# ── the colours, as RPM.Core/Viz reads them off a picture ──

def dominant(pixels):
    """CoverColor.DominantOf: the biggest bucket, in 32 levels an axis."""
    buckets = {}
    for r, g, b in pixels:
        if max(r, g, b) < 24 or min(r, g, b) > 232:
            continue
        key = (r >> 5 << 10) | (g >> 5 << 5) | (b >> 5)
        c, sr, sg, sb = buckets.get(key, (0, 0, 0, 0))
        buckets[key] = (c + 1, sr + r, sg + g, sb + b)
    if not buckets:
        return None
    c, sr, sg, sb = max(buckets.values(), key=lambda v: v[0])
    return (sr // c, sg // c, sb // c)


def vivid_buckets(pixels):
    """CoverColor.HighlightOf's buckets, scored vivid, present and bright."""
    buckets = {}
    for r, g, b in pixels:
        hi, lo = max(r, g, b), min(r, g, b)
        if hi < 40 or hi - lo < 24:
            continue
        key = (r >> 5 << 10) | (g >> 5 << 5) | (b >> 5)
        c, sr, sg, sb = buckets.get(key, (0, 0, 0, 0))
        buckets[key] = (c + 1, sr + r, sg + g, sb + b)

    scored = []
    for c, sr, sg, sb in buckets.values():
        r, g, b = sr / c, sg / c, sb / c
        hi, lo = max(r, g, b), min(r, g, b)
        sat = 0 if hi <= 0 else (hi - lo) / hi
        scored.append((sat * math.sqrt(c) * (hi / 255.0), (int(r), int(g), int(b))))
    scored.sort(reverse=True, key=lambda x: x[0])
    return [c for _, c in scored]


def unit(c):
    n = math.sqrt(sum(x * x for x in c))
    return [x / n for x in c] if n > 1e-4 else [0.0, 0.0, 0.0]


def same(a, b):
    """RoomPalette.Same: compared as directions, so light and dark of one hue are one."""
    ua, ub = unit(a), unit(b)
    return math.dist(ua, ub) < 0.22


def clamp01(v):
    return [min(1.0, max(0.0, x)) for x in v]


def vividf(c, saturate, lift):
    """RoomPalette.Vivid: off its own grey, then set to a working brightness."""
    luma = c[0] * 0.2126 + c[1] * 0.7152 + c[2] * 0.0722
    v = clamp01([luma + (x - luma) * saturate for x in c])
    peak = max(v)
    if peak > 1e-4:
        v = [x * lift / peak for x in v]
    return clamp01(v)


def hue(c, degrees):
    """RoomPalette.Hue: the luma-preserving rotation about the grey axis."""
    a = math.radians(degrees)
    k, s = math.cos(a), math.sin(a)
    m = ((0.213 + k * 0.787 - s * 0.213, 0.715 - k * 0.715 - s * 0.715, 0.072 - k * 0.072 + s * 0.928),
         (0.213 - k * 0.213 + s * 0.143, 0.715 + k * 0.285 + s * 0.140, 0.072 - k * 0.072 - s * 0.283),
         (0.213 - k * 0.213 - s * 0.787, 0.715 - k * 0.715 + s * 0.715, 0.072 + k * 0.928 + s * 0.072))
    return clamp01([sum(c[i] * row[i] for i in range(3)) for row in m])


GROUND_FALLBACK = [0.012, 0.030, 0.070]
FALLBACK = {"ground": GROUND_FALLBACK, "tint": [0.012, 0.443, 0.816],
            "glow": [0.02, 0.78, 1.0], "shade": [0.62, 0.16, 0.72]}


def room_palette(tint, glow, shade):
    """RoomPalette.From, transcribed. See RPM.Core/Viz/RoomPalette.cs."""
    base = tint or glow
    accent = glow or base
    if base is None or accent is None:
        return dict(FALLBACK)

    out_tint = vividf(base, 1.35, 0.62)
    out_glow = vividf(accent, 1.5, 0.90)
    spare = shade if shade and not same(shade, base) and not same(shade, accent) else None

    if same(out_tint, out_glow):
        if spare:
            out_glow = vividf(spare, 1.5, 0.90)
            spare = None
        else:
            out_glow = vividf(hue(accent, 42.0), 1.5, 0.90)

    out_shade = vividf(spare, 1.35, 0.76) if spare else vividf(hue(base, 152.0), 1.4, 0.74)
    lit = vividf(base, 1.1, 0.16)
    out_ground = [lit[i] * 0.55 + GROUND_FALLBACK[i] * 0.45 for i in range(3)]
    return {"ground": out_ground, "tint": out_tint, "glow": out_glow, "shade": out_shade}


def hexof(v):
    return "#%02x%02x%02x" % tuple(min(255, max(0, round(x * 255))) for x in v)


# ── the pictures ──

def fetch(url):
    """One picture, cached under its own address. Fetched once, ever."""
    ART.mkdir(parents=True, exist_ok=True)
    name = hashlib.sha1(url.encode()).hexdigest()[:16] + ".jpg"
    path = ART / name
    if path.exists() and path.stat().st_size > 0:
        return name
    try:
        req = urllib.request.Request(url, headers={"User-Agent": "rpm-roomlab/1"})
        with urllib.request.urlopen(req, timeout=20) as r:
            data = r.read()
        if len(data) < 1024:
            return None
        path.write_bytes(data)
        return name
    except Exception as e:
        print(f"  ! {url[:70]}: {e}")
        return None


def spread(have, want):
    """As many DIFFERENT pictures as asked for, one artist at a time.

    A second photograph of an artist is only reached for once every artist on the list
    has given one, and a third once every artist has given two. Taking each artist's
    best and then stopping left the lab short of pictures for a playlist with six
    artists and twelve frames to fill, and the frames repeated: the same face twice in
    one header, while that artist had seven other photographs sitting unused.
    """
    out, seen, depth = [], set(), 0
    while len(out) < want:
        added = 0
        for artist, urls in have:
            if depth >= len(urls):
                continue
            url = urls[depth]
            # Two artists on one list can be filed under names that share a picture,
            # and one artist's list can carry the same address twice. Either way the
            # header would show it twice, so the address is what is checked.
            if url in seen:
                continue
            seen.add(url)
            out.append((artist, url))
            added += 1
            if len(out) >= want:
                break
        if added == 0:
            break          # every artist is exhausted; there are no more pictures
        depth += 1
    return out


def colours_of(names):
    """The three colours a set of pictures gives up, in RoomPalette's terms."""
    from PIL import Image
    pixels = []
    for name in names[:4]:
        try:
            # 48 square, which is the size CoverColor.Small decodes to.
            im = Image.open(ART / name).convert("RGB").resize((48, 48))
            pixels.extend(im.getdata())
        except Exception:
            continue
    if not pixels:
        return dict(FALLBACK)

    dom = dominant(pixels)
    vivids = vivid_buckets(pixels)
    tint = [x / 255 for x in dom] if dom else None
    glow = [x / 255 for x in vivids[0]] if vivids else None
    # The next colour that is genuinely a third rather than one of the two in hand.
    shade = None
    for c in vivids[1:]:
        v = [x / 255 for x in c]
        if (tint is None or not same(v, tint)) and (glow is None or not same(v, glow)):
            shade = v
            break
    return room_palette(tint, glow, shade)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--peer", default="GIOVANNI", help="which paired machine's playlists")
    ap.add_argument("--list", action="store_true", help="show the playlists and stop")
    ap.add_argument("--least", type=int, default=5, help="fewest artists with a picture")
    ap.add_argument("--most", type=int, default=12, help="most pictures kept per playlist")
    args = ap.parse_args()

    peer_name, peer = peer_db(args.peer)
    if not os.environ.get("RPM_LIBRARY") or not OWN.is_file():
        raise SystemExit("set RPM_LIBRARY to a library.db (<music folder>/.rpm/library.db)")
    own = copy_db(OWN, CACHE / "own.db")

    # The best picture for each artist this machine has fetched one for, in the order
    # the ranker left them.
    art = {}
    for key, url in own.execute(
            "select artist_key, url from artist_art where hidden=0 order by artist_key, position"):
        art.setdefault(key.lower(), []).append(url)

    rows = peer.execute("""
        select n.name, t.artist, count(*) c from pl_item i
          join pl_node n on n.id = i.playlist_id
          join tracks t on t.rpm_id = i.rpm_id
         where n.kind = 'playlist' and t.artist is not null and t.artist <> ''
         group by n.id, t.artist order by n.name, c desc""")

    per = {}
    for name, artist, count in rows:
        per.setdefault(name, []).append((artist, count))

    # What the header writes under the name: how many songs and how long. Off the
    # tracks themselves rather than counted from the items, so a row pointing at a
    # track the mirror does not have is not counted as a song it can play.
    totals = {}
    for name, songs, seconds in peer.execute("""
        select n.name, count(*), coalesce(sum(t.duration_seconds), 0) from pl_item i
          join pl_node n on n.id = i.playlist_id
          join tracks t on t.rpm_id = i.rpm_id
         where n.kind = 'playlist' group by n.id"""):
        totals[name] = (songs, int(seconds))

    playlists = []
    for name, artists in sorted(per.items()):
        have = [(a, art[a.lower()]) for a, _ in artists if a.lower() in art]
        if len(have) >= args.least:
            playlists.append((name, spread(have, args.most), len(artists)))

    if args.list:
        print(f"{peer_name}: {len(per)} playlists, {len(playlists)} with "
              f"{args.least}+ artists pictured")
        for name, have, total in playlists:
            print(f"  {len(have):3d} of {total:3d}  {name}")
        return

    out = []
    for name, have, total in playlists:
        print(f"── {name} ({len(have)} of {total} artists)")
        pictures = []
        for artist, url in have:
            got = fetch(url)
            if got:
                pictures.append({"artist": artist, "file": got})
        if len(pictures) < args.least:
            print("   too few pictures came back; skipped")
            continue
        palette = colours_of([p["file"] for p in pictures])
        print("   " + "  ".join(f"{k} {hexof(v)}" for k, v in palette.items()))
        songs, seconds = totals.get(name, (0, 0))
        acts = [a for a, _ in per[name]]
        out.append({"name": name, "artists": total,
                    "songs": songs, "seconds": seconds,
                    # The three most present acts, and how many others there are:
                    # what the header's "Featuring" line is made of.
                    "featuring": acts[:3], "moreActs": max(0, len(acts) - 3),
                    "palette": {k: hexof(v) for k, v in palette.items()},
                    "pictures": pictures})

    OUT.write_text(json.dumps({"peer": peer_name, "playlists": out}, indent=1))
    print(f"\n{OUT.name}: {len(out)} playlists, "
          f"{sum(len(p['pictures']) for p in out)} pictures in {ART.name}/")


if __name__ == "__main__":
    main()
