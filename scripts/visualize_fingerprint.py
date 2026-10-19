#!/usr/bin/env python3
"""
Draws a Chromaprint fingerprint as two images:

  1. A 1-bit "barcode": 32 rows by N columns, where each pixel is one bit of the
     sub-fingerprint. Black is a set bit, white a clear bit. The song's structure
     over time is visible: stable sections, transitions, repeats.

  2. A "gradient strip": N columns by 1 thick row, where each column's colour is
     derived from the full 32-bit sub-fingerprint by an HSL mapping. Each song gives
     its own band of colour that changes as the music goes on.

Usage:
    python3 visualize_fingerprint.py <audio_file> [<audio_file2>...]

Each input file produces two output PNGs in /tmp:
    /tmp/fp_<basename>_bits.png
    /tmp/fp_<basename>_gradient.png

If multiple files are given, also produces a comparison stack:
    /tmp/fp_compare_gradients.png
"""
import sys
import json
import subprocess
import colorsys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont


def fpcalc_raw(path):
    """Run fpcalc and return (duration, list of int32 sub-fingerprints)."""
    result = subprocess.run(
        ["fpcalc", "-json", "-raw", "-signed", "-length", "0", path],
        capture_output=True, text=True, check=True,
    )
    data = json.loads(result.stdout)
    return data["duration"], data["fingerprint"]


def render_bitmap(subfps, scale=2):
    """
    Render a 32xN bit-barcode image. Each row is one bit position (top = bit 0).
    Each column is one frame in time. Black = bit set, white = bit clear.
    `scale` enlarges each pixel so that the image is easier to see.
    """
    n = len(subfps)
    img = Image.new("L", (n, 32), color=255)
    pixels = img.load()
    for x, v in enumerate(subfps):
        u = v if v >= 0 else v + (1 << 32)
        for y in range(32):
            if (u >> y) & 1:
                pixels[x, y] = 0
    if scale != 1:
        img = img.resize((n * scale, 32 * scale), Image.NEAREST)
    return img


def render_gradient(subfps, height=80, scale=2):
    """
    Render an Nxheight gradient strip. The colour of each column comes from the
    full 32-bit sub-fingerprint: the integer is mapped to a hue, and the number of
    set bits changes the saturation and value. Adjacent columns have similar
    colours because adjacent sub-fingerprints share most bits. This shows that
    the music changes little in 125 ms.
    """
    n = len(subfps)
    img = Image.new("RGB", (n, height), color=(255, 255, 255))
    pixels = img.load()

    def color_from_subfp(v):
        u = v if v >= 0 else v + (1 << 32)
        # Hue from the low 16 bits, saturation from the popcount, value from the high 16 bits.
        # Related sub-fingerprints get similar colours, and different songs get different
        # palettes.
        hue = (u & 0xFFFF) / 65535.0
        popcount = bin(u).count("1")  # 0..32
        sat = 0.5 + (popcount / 32.0) * 0.5
        val = 0.4 + ((u >> 16) & 0xFFFF) / 65535.0 * 0.6
        r, g, b = colorsys.hsv_to_rgb(hue, sat, val)
        return (int(r * 255), int(g * 255), int(b * 255))

    for x, v in enumerate(subfps):
        c = color_from_subfp(v)
        for y in range(height):
            pixels[x, y] = c

    if scale != 1:
        img = img.resize((n * scale, height * scale), Image.NEAREST)
    return img


def render_comparison(songs, height=60, scale=2, label_width=300):
    """
    Stack several gradient strips with labels, for comparison.
    `songs` is a list of (label, subfps) tuples.
    """
    if not songs:
        return None
    max_n = max(len(s[1]) for s in songs)
    rows = len(songs)
    spacer = 8
    total_height = (height + spacer) * rows + spacer
    img = Image.new("RGB", (label_width + max_n, total_height), color=(20, 20, 24))
    draw = ImageDraw.Draw(img)
    try:
        font = ImageFont.truetype("/System/Library/Fonts/Helvetica.ttc", 14)
    except Exception:
        font = ImageFont.load_default()

    for i, (label, subfps) in enumerate(songs):
        strip = render_gradient(subfps, height=height, scale=1)
        y0 = spacer + i * (height + spacer)
        img.paste(strip, (label_width, y0))
        draw.text((10, y0 + 4), label, fill=(230, 230, 230), font=font)
        draw.text((10, y0 + 22), f"{len(subfps)} sub-fps", fill=(160, 160, 160), font=font)

    if scale != 1:
        img = img.resize(
            (img.width * scale, img.height * scale), Image.NEAREST
        )
    return img


def main():
    if len(sys.argv) < 2:
        print(__doc__, file=sys.stderr)
        sys.exit(1)

    paths = sys.argv[1:]
    songs_for_comparison = []

    for p in paths:
        if not Path(p).is_file():
            print(f"skipping (not a file): {p}", file=sys.stderr)
            continue
        basename = Path(p).stem.replace(" ", "_").replace("/", "_")[:60]
        print(f"[{basename}] running fpcalc...")
        duration, fps = fpcalc_raw(p)
        print(f"  duration {duration:.1f}s, {len(fps)} sub-fingerprints")

        bits_path = f"/tmp/fp_{basename}_bits.png"
        grad_path = f"/tmp/fp_{basename}_gradient.png"
        render_bitmap(fps, scale=2).save(bits_path)
        render_gradient(fps, height=80, scale=1).save(grad_path)
        print(f"  saved bit barcode → {bits_path}")
        print(f"  saved gradient    → {grad_path}")
        songs_for_comparison.append((basename, fps))

    if len(songs_for_comparison) > 1:
        cmp_img = render_comparison(songs_for_comparison, height=60, scale=1)
        cmp_path = "/tmp/fp_compare_gradients.png"
        cmp_img.save(cmp_path)
        print(f"\ncomparison → {cmp_path}")


if __name__ == "__main__":
    main()
