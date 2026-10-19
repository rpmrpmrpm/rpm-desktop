# Asks Deezer whether each track's lyrics are explicit, for every track whose flag is empty and then
# every track flagged Clean (those only for the report; explicitmark never changes a flag that is set).
# Writes one JSON line per track. Read-only against the library database.
#
# Why Deezer and not ITunesAdvisoryClient: that client matches whole albums by name and track count.
# On a sample library it matched 14 of 25 albums. A per-song search matched 35 of 40 tracks already
# flagged Explicit, and none of them was called clean.
#
# Which versions of a song count, and how many, is in verdict(). Every candidate is kept in the output
# ("cands"), so the rule can be changed and the answers recomputed without asking Deezer again.
#
# Resumable: a track whose last line is a miss or an error, or whose answer has an explicit version
# but no stored candidates, is asked again. The last line per id is the answer. Three
# workers, one request every 0.12 s between them: under Deezer's 50 per 5 seconds.
#
#   python tools/explicitmark/deezer_scan.py "<library root>" scan.jsonl
#   python tools/explicitmark/deezer_scan.py --recompute scan.jsonl final.jsonl
#
# --recompute asks nothing: it applies verdict() again to the candidates already stored, one line per
# track, after verdict() has been changed.
import sqlite3, json, time, urllib.request, urllib.parse, re, os, sys, threading, unicodedata
from concurrent.futures import ThreadPoolExecutor

RECOMPUTE = len(sys.argv) == 4 and sys.argv[1] == "--recompute"
if len(sys.argv) != 3 and not RECOMPUTE:
    sys.exit("usage: deezer_scan.py <library root> <out.jsonl>\n"
             "       deezer_scan.py --recompute <in.jsonl> <out.jsonl>")

CLEAN_HINT = re.compile(r"\b(clean|edited|radio edit|radio version)\b", re.I)
CLASSICAL = re.compile(r"classical|opera|baroque|symphon|chamber", re.I)
SPLIT = re.compile(r"\s*(?:,|;|/|&|\bfeat\.?|\bft\.?|\bfeaturing\b|\bx\b|\band\b|\bwith\b|\bvs\.?)\s*", re.I)
FEAT = re.compile(r"\s+(?:feat\.?|ft\.?|featuring)\s+.*$", re.I)

# Versions whose words differ from the record's, or that have none. Deezer flags live recordings for
# what is said on stage, and instrumentals as clean, so a candidate of one of these kinds only counts
# for a track that is the same kind.
KINDS = {
    "live": re.compile(r"\blive\b", re.I),
    "instrumental": re.compile(r"\binstrumental\b", re.I),
    "karaoke": re.compile(r"\bkaraoke\b", re.I),
    "acapella": re.compile(r"\ba ?cap+el+a\b", re.I),
    "demo": re.compile(r"\bdemo\b", re.I),
}

def kinds(title):
    return {k for k, rx in KINDS.items() if rx.search(title or "")}

def fold(s):
    s = unicodedata.normalize("NFKD", s or "")
    return "".join(c for c in s.lower() if c.isalnum() and not unicodedata.combining(c))

def strip_title(t):
    t = (t or "").replace("\U0001f174", "")
    t = re.sub(r"\s*[\(\[].*?[\)\]]", "", t)
    t = FEAT.sub("", t)
    return t.strip()

def base(t):
    return fold(re.sub(r"\s+-\s+.*$", "", strip_title(t)))

def artists(a):
    a = re.sub(r"\s*\(\d+\)", "", a or "").replace("*", "")
    return [p for p in (x.strip() for x in SPLIT.split(a)) if p]

# Deezer's explicit_content_lyrics: 0 not explicit, 1 explicit, 3 an edited (clean) version, 2 and 6
# no answer. A track is explicit when a version of its own kind is, and the explicit and edited versions
# of that kind are at least as many as the plainly clean ones. One explicit version among several clean
# ones is a compilation flagged as a whole (Ray Charles, "Georgia on My Mind": one explicit, five clean).
def verdict(title, cands):
    mine = kinds(title)
    if mine & {"instrumental", "karaoke"}:
        return "instrumental"
    if not cands:
        return "miss"
    # The nearest versions that give an answer (0, 1 or 3): exactly this kind (a live track against
    # live versions), then any version no further from the record than this one, then any version
    # with words. A studio version that says 0 outranks a live one that says 1 (Beck, "Hotwax"); a
    # studio version with no answer (6) leaves it to the live ones (Slipknot, "Duality"). The last
    # tier only fills in for a version of this kind that Deezer has and says nothing about: with no
    # such version at all, a live recording's flag is not evidence about this one.
    near = [c for c in cands if kinds(c["t"]) <= mine]
    tiers = [
        [c for c in cands if kinds(c["t"]) == mine],
        near,
        [c for c in cands if not kinds(c["t"]) & {"instrumental", "karaoke"}] if near else [],
    ]
    codes = next(([c["e"] for c in tier] for tier in tiers if any(c["e"] in (0, 1, 3) for c in tier)), [])
    explicit, edited, clean = codes.count(1), codes.count(3), codes.count(0)
    return "explicit" if explicit > 0 and explicit + edited >= clean else "not"

# One request every 0.12 s across all workers.
gate = threading.Lock(); next_at = [0.0]
def get(url):
    for attempt in range(6):
        with gate:
            wait = next_at[0] - time.monotonic()
            if wait > 0: time.sleep(wait)
            next_at[0] = time.monotonic() + 0.12
        try:
            with urllib.request.urlopen(url, timeout=15) as r:
                data = json.load(r)
        except Exception:
            time.sleep(3 * (attempt + 1)); continue
        if isinstance(data, dict) and "error" in data:
            time.sleep(6 * (attempt + 1)); continue
        return data
    return None

cache, cache_lock = {}, threading.Lock()
def candidates(artist, title, album, dur):
    """Deezer tracks with the same title by the same artist (or on an album of the same name), as
    (state, list). The artist is compared whole after folding accents: "Bjork" is "Björk", and
    "Savage" is not "D Savage". The album is the fallback for an artist Deezer files under an alias
    whose name folds to nothing ("¥$")."""
    names = [fold(n) for n in artists(artist)]
    names = [n for n in names if n]
    if not names: return "noartist", []
    key = (names[0], base(title), fold(album))
    with cache_lock:
        if key in cache: return cache[key]
    want, want_album = base(title), base(album)

    def matches(data):
        out = []
        for r in (data or {}).get("data", []):
            if base(r.get("title")) != want: continue
            who = fold(r["artist"]["name"])
            on = base((r.get("album") or {}).get("title"))
            if not ((who and who in names) or (want_album and on == want_album)): continue
            out.append(r)
        return out

    clean = strip_title(title)
    # A plain query. Deezer's field syntax (artist:"x" track:"y") returns nothing at all
    # for songs that the plain query finds first.
    data = get("https://api.deezer.com/search?limit=50&q=" + urllib.parse.quote(f"{artists(artist)[0]} {clean}"))
    if data is None: return "error", []
    hits = matches(data)
    if not hits:
        data = get("https://api.deezer.com/search?limit=50&q=" + urllib.parse.quote(clean))
        if data is None: return "error", []
        hits = matches(data)

    close = [r for r in hits if dur and abs(r.get("duration", 0) - dur) <= 8]
    found = [{"t": r["title"], "a": r["artist"]["name"], "al": (r.get("album") or {}).get("title"),
              "d": r.get("duration"), "e": r.get("explicit_content_lyrics")} for r in (close or hits)]
    result = ("ok" if found else "miss", found)
    with cache_lock:
        cache[key] = result
    return result

def recompute(src, dst):
    last = {}
    with open(src, encoding="utf-8") as f:
        for line in f:
            r = json.loads(line); last[r["id"]] = r
    changed = 0
    with open(dst, "w", encoding="utf-8") as out:
        for r in last.values():
            if "cands" in r and r["verdict"] not in ("cleanhint", "classical", "error", "noartist"):
                again = verdict(r["title"], r["cands"])
                changed += again != r["verdict"]
                r["verdict"] = again
            out.write(json.dumps(r, ensure_ascii=False) + "\n")
    print(f"{len(last)} tracks, {changed} verdicts changed", flush=True)

if RECOMPUTE:
    recompute(sys.argv[2], sys.argv[3])
else:
    ROOT, OUT = sys.argv[1], sys.argv[2]
    db = sqlite3.connect("file:" + os.path.join(ROOT, ".rpm", "library.db").replace("\\", "/") + "?mode=ro", uri=True)
    rows = db.execute("""
      SELECT id, rpm_id, relative_path, cloud_state, COALESCE(advisory, 0), artist, album_artist, title,
             duration_seconds, album, genre
      FROM tracks
      WHERE COALESCE(advisory, 0) IN (0, 2) AND title IS NOT NULL AND title <> ''
      ORDER BY COALESCE(advisory, 0), id""").fetchall()

    last = {}
    if os.path.exists(OUT):
        with open(OUT, encoding="utf-8") as f:
            for line in f:
                try:
                    r = json.loads(line); last[r["id"]] = r
                except Exception: pass

    def answered(r):
        if r["verdict"] in ("miss", "error"): return False
        if "cands" in r: return True
        return not (r["verdict"] == "explicit" or 1 in (r.get("codes") or []))

    done = {i for i, r in last.items() if answered(r)}
    todo = [r for r in rows if r[0] not in done]
    print(f"{len(rows)} tracks in scope, {len(done)} already answered, {len(todo)} to go", flush=True)
    out = open(OUT, "a", encoding="utf-8"); out_lock = threading.Lock(); count = [0]

    def one(row):
        tid, rpm, rel, cs, adv, artist, aartist, title, dur, album, genre = row
        cands = []
        if CLEAN_HINT.search(title or ""):
            answer = "cleanhint"
        elif CLASSICAL.search(genre or ""):
            answer = "classical"
        else:
            state, cands = candidates(artist or aartist, title, album, dur)
            if state in ("miss", "noartist") and aartist and aartist != artist:
                state, cands = candidates(aartist, title, album, dur)
            answer = verdict(title, cands) if state == "ok" else state
        line = json.dumps({"id": tid, "rpm": rpm, "path": rel, "cloud": cs, "adv": adv, "artist": artist,
                           "title": title, "album": album, "genre": genre, "verdict": answer,
                           "cands": cands}, ensure_ascii=False)
        with out_lock:
            out.write(line + "\n"); count[0] += 1
            if count[0] % 500 == 0:
                out.flush(); print(f"{count[0]}/{len(todo)}", flush=True)

    with ThreadPoolExecutor(max_workers=3) as pool:
        list(pool.map(one, todo))
    out.close()
    print("finished", flush=True)
