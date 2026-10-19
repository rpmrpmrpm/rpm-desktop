#!/usr/bin/env python3
"""
Render a Chromaprint fingerprint as a background SVG: dark navy base, sky-blue and
indigo glows, subtle grain. The base gradient is coloured from the song's own
fingerprint, so each song gets its own background.

Usage:
    python3 fingerprint_to_background_svg.py <audio_file> [--out OUT.svg]
"""
import sys
import json
import subprocess
import argparse
from pathlib import Path


# Canvas size of the background SVG
WIDTH = 1366
HEIGHT = 868


def fpcalc_raw(path):
    result = subprocess.run(
        ["fpcalc", "-json", "-raw", "-signed", "-length", "0", path],
        capture_output=True, text=True, check=True,
    )
    data = json.loads(result.stdout)
    return data["duration"], data["fingerprint"]


def popcount(v):
    if v < 0:
        v += 1 << 32
    return bin(v).count("1")


def smooth(values, window):
    """Centred moving average."""
    n = len(values)
    out = [0.0] * n
    half = window // 2
    for i in range(n):
        lo = max(0, i - half)
        hi = min(n, i + half + 1)
        out[i] = sum(values[lo:hi]) / (hi - lo)
    return out


def downsample(values, target):
    """Reduces a long sequence to `target` points by averaging buckets."""
    n = len(values)
    if n <= target:
        return values
    out = []
    for i in range(target):
        lo = i * n // target
        hi = (i + 1) * n // target
        bucket = values[lo:hi]
        out.append(sum(bucket) / len(bucket) if bucket else 0)
    return out


def normalize(values, lo_pct=2.0, hi_pct=98.0):
    """Normalises to 0..1. Percentile clipping stops a few outliers from flattening the rest."""
    s = sorted(values)
    n = len(s)
    lo = s[int(n * lo_pct / 100)]
    hi = s[int(n * hi_pct / 100) - 1]
    span = hi - lo if hi > lo else 1
    return [max(0.0, min(1.0, (v - lo) / span)) for v in values]


def hex_to_rgb(hex_str):
    h = hex_str.lstrip("#")
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16))


def lerp_rgb(c1, c2, t):
    return (
        round(c1[0] + (c2[0] - c1[0]) * t),
        round(c1[1] + (c2[1] - c1[1]) * t),
        round(c1[2] + (c2[2] - c1[2]) * t),
    )


def rgb_to_hex(rgb):
    return f"#{rgb[0]:02x}{rgb[1]:02x}{rgb[2]:02x}"


def build_data_driven_gradient_stops(samples, trough_hex="#1a2638", peak_hex="#060a13"):
    """
    Builds exactly 3 SVG <stop> elements (0%, 50%, 100%). Each stop's colour comes
    from the average intensity of one third of the song:
      - 0%: average intensity of the first third
      - 50%: average intensity of the middle third
      - 100%: average intensity of the last third

    Three stops always give a smooth gradient (linear interpolation between three
    stops cannot band), and the colours still follow the overall shape of the song.
    The palette range is narrow, so the result is a gentle shading of the base
    colour and not a different layer.
    """
    trough = hex_to_rgb(trough_hex)
    peak = hex_to_rgb(peak_hex)
    n = len(samples)
    if n == 0:
        # Fixed stops, used when there are no samples
        return (
            '      <stop offset="0%"   style="stop-color:#141d2b;stop-opacity:1" />\n'
            '      <stop offset="50%"  style="stop-color:#0e1520;stop-opacity:1" />\n'
            '      <stop offset="100%" style="stop-color:#080c15;stop-opacity:1" />'
        )

    third = n // 3
    avg_first = sum(samples[:third]) / max(1, third)
    avg_mid = sum(samples[third:2 * third]) / max(1, third)
    avg_last = sum(samples[2 * third:]) / max(1, n - 2 * third)

    c0 = rgb_to_hex(lerp_rgb(trough, peak, avg_first))
    c1 = rgb_to_hex(lerp_rgb(trough, peak, avg_mid))
    c2 = rgb_to_hex(lerp_rgb(trough, peak, avg_last))
    return (
        f'      <stop offset="0%"   style="stop-color:{c0};stop-opacity:1" />\n'
        f'      <stop offset="50%"  style="stop-color:{c1};stop-opacity:1" />\n'
        f'      <stop offset="100%" style="stop-color:{c2};stop-opacity:1" />'
    )


def render_svg(samples, title=""):
    """
    Renders the full SVG. The base gradient is data-driven: each stop's colour is
    interpolated between a lighter blue (low fingerprint intensity) and a deeper
    navy (high intensity). Peaks in the song's harmonic density become darker
    bands; quiet sections stay lighter.
    """
    data_stops = build_data_driven_gradient_stops(samples)

    return f'''<svg xmlns="http://www.w3.org/2000/svg" width="{WIDTH}" height="{HEIGHT}" viewBox="0 0 {WIDTH} {HEIGHT}">
  <defs>
    <!-- Base: dark navy with a visible blue tint (3 stops, diagonal). The 3 colors
         here are derived from the song: avg intensity of first third / middle
         third / last third of the audio. Gentle song-shaped tilt of the same
         original gradient. -->
    <linearGradient id="base" x1="0%" y1="0%" x2="100%" y2="100%">
{data_stops}
    </linearGradient>

    <!-- Top-left accent: brighter sky-blue glow -->
    <radialGradient id="glowTopLeft" cx="0%" cy="0%" r="65%">
      <stop offset="0%"   style="stop-color:#38bdf8;stop-opacity:0.22" />
      <stop offset="100%" style="stop-color:#38bdf8;stop-opacity:0" />
    </radialGradient>

    <!-- Bottom-right accent: lighter indigo glow -->
    <radialGradient id="glowBottomRight" cx="100%" cy="100%" r="60%">
      <stop offset="0%"   style="stop-color:#818cf8;stop-opacity:0.20" />
      <stop offset="100%" style="stop-color:#818cf8;stop-opacity:0" />
    </radialGradient>

    <!-- Center lift — keeps the middle from feeling too flat -->
    <radialGradient id="glowCenter" cx="50%" cy="45%" r="45%">
      <stop offset="0%"   style="stop-color:#1e3a5f;stop-opacity:0.45" />
      <stop offset="100%" style="stop-color:#1e3a5f;stop-opacity:0" />
    </radialGradient>

    <!-- Subtle noise/grain filter -->
    <filter id="grain" x="0%" y="0%" width="100%" height="100%">
      <feTurbulence type="fractalNoise" baseFrequency="0.65" numOctaves="3" stitchTiles="stitch" result="noise"/>
      <feColorMatrix type="saturate" values="0" in="noise" result="grayNoise"/>
      <feBlend in="SourceGraphic" in2="grayNoise" mode="overlay" result="blended"/>
      <feComposite in="blended" in2="SourceGraphic" operator="in"/>
    </filter>

    <linearGradient id="fadeBottom" x1="0%" y1="0%" x2="0%" y2="100%">
      <stop offset="65%" style="stop-color:#000000;stop-opacity:0" />
      <stop offset="100%" style="stop-color:#000000;stop-opacity:0.35" />
    </linearGradient>
  </defs>

  <!-- Base, glows, fade, grain -->
  <rect width="{WIDTH}" height="{HEIGHT}" fill="url(#base)" />
  <rect width="{WIDTH}" height="{HEIGHT}" fill="url(#glowTopLeft)" />
  <rect width="{WIDTH}" height="{HEIGHT}" fill="url(#glowBottomRight)" />
  <rect width="{WIDTH}" height="{HEIGHT}" fill="url(#glowCenter)" />
  <rect width="{WIDTH}" height="{HEIGHT}" fill="url(#fadeBottom)" />
  <rect width="{WIDTH}" height="{HEIGHT}" fill="transparent" filter="url(#grain)" opacity="0.03" />

  {f'<!-- {title} -->' if title else ''}
</svg>
'''


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("file", help="audio file to fingerprint")
    p.add_argument("--out", default=None, help="output SVG path (default: tmp/fp_<name>_bg.svg)")
    p.add_argument("--smoothing", type=int, default=200,
                   help="moving-average window in frames (default 200 ≈ 25s)")
    p.add_argument("--samples", type=int, default=8,
                   help="number of gradient stops (default 8 — keep low to avoid banding; "
                        "background.svg uses 3 and looks completely smooth)")
    args = p.parse_args()

    if not Path(args.file).is_file():
        print(f"error: not a file: {args.file}", file=sys.stderr)
        sys.exit(1)

    print(f"running fpcalc on {Path(args.file).name}...")
    duration, fps = fpcalc_raw(args.file)
    print(f"  duration {duration:.1f}s, {len(fps)} sub-fingerprints")

    # Per-frame "intensity" signal: the popcount of each sub-fingerprint.
    # Popcount follows how active and harmonically dense the slice is: quiet
    # slices have lower popcounts than dense slices.
    intensities = [popcount(v) for v in fps]
    print(f"  popcount range: {min(intensities)}..{max(intensities)}")

    # Smooth heavily so the curve has no sharp jumps
    smoothed = smooth(intensities, args.smoothing)
    # Downsample to a few points, which the gradient stops then average by thirds
    sampled = downsample(smoothed, args.samples)
    # Percentile-clipped normalisation, so a single outlier does not flatten the rest
    normalized = normalize(sampled)

    title = f"{Path(args.file).stem} — {duration:.0f}s — {len(fps)} sub-fingerprints"
    svg = render_svg(normalized, title=title)

    if args.out:
        out_path = Path(args.out)
    else:
        name = Path(args.file).stem.replace(" ", "_").replace("/", "_")[:60]
        out_path = Path("tmp") / f"fp_{name}_bg.svg"
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(svg)
    print(f"  saved → {out_path}  ({out_path.stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
