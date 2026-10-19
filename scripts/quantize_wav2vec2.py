#!/usr/bin/env python3
"""Writes an eight-bit copy of the exported wav2vec2 weights.

The aligner runs once per lyric line while a track plays, on a machine that is also decoding audio
and drawing a visualiser. The feature is only decoration, so it must finish in the gap between two
lines on an ordinary laptop. Dynamic quantisation stores the weight matrices as int8 and multiplies
them as integers, which is several times faster on an x86 processor with VNNI. The file shrinks
from about 360 MB to about 95 MB.

Nothing is retrained and no data is needed: only the weights are quantised, and the activations are
scaled on each call from their own range.

The accuracy cost is checked by this script, and NOT by comparing logits. This model is only read by
a forced aligner, which needs one thing: the likeliest path through the frames must still spell the
same letters in the same places. A model can shift every logit by a tenth and align the same way, or
shift one frame and move a word. So the two models are compared on their frame-by-frame verdicts and
on a greedy decode, which is what the alignment is built from.

Run it from the repo root with the venv that has onnxruntime in it. It reads
RPM.Desktop/Assets/OnnxModels/wav2vec2.onnx and writes wav2vec2.int8.onnx next to it.

    python3 scripts/quantize_wav2vec2.py
"""

from __future__ import annotations

import argparse
import json
import pathlib
import sys

import numpy as np

SAMPLE_RATE = 16000

# Long enough to cover several lyric lines of frames, so the comparison below uses a few thousand
# verdicts and not just a handful.
CHECK_SECONDS = 20


def quantise(source: pathlib.Path, destination: pathlib.Path) -> None:
    from onnxruntime.quantization import QuantType, quantize_dynamic

    print(f"quantising {source.name} …")
    quantize_dynamic(
        model_input=str(source),
        model_output=str(destination),
        weight_type=QuantType.QInt8,
        # The convolutional front end is left alone. It is a small share of the weights, and it reads
        # the waveform itself, where eight bits costs the most accuracy and saves the least.
        op_types_to_quantize=["MatMul", "Attention"],
        extra_options={"MatMulConstBOnly": True},
        # reduce_range stays off. It is the ONNX Runtime remedy for u8 x s8 saturation on AVX2 without
        # VNNI, but it moved words further from the float32 result on Apple silicon and barely
        # changed the AVX2 result. With the onnxruntime 1.30 quantiser, this call reproduces the
        # shipped file byte for byte.
    )

    was = source.stat().st_size / (1024 * 1024)
    now = destination.stat().st_size / (1024 * 1024)
    print(f"{was:.0f} MB became {now:.0f} MB")


def check(source: pathlib.Path, destination: pathlib.Path) -> None:
    """Runs both models on the same noise and compares what the aligner reads."""
    import onnxruntime as ort

    rng = np.random.default_rng(0)
    audio = (rng.standard_normal((1, SAMPLE_RATE * CHECK_SECONDS)) * 0.1).astype(np.float32)

    def logits(path: pathlib.Path) -> np.ndarray:
        session = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
        return session.run(["logits"], {"audio": audio})[0][0]

    full = logits(source)
    small = logits(destination)

    if full.shape != small.shape:
        sys.exit(f"shape changed: {full.shape} became {small.shape}")

    # The verdict per frame: which token the model rates highest. A path is built from these, so
    # agreement here is the number that matters.
    agree = float((full.argmax(-1) == small.argmax(-1)).mean())

    # The greedy decode, which has the same shape as the alignment: repeats collapsed, blanks dropped.
    def decode(x: np.ndarray) -> list[int]:
        best = x.argmax(-1)
        out: list[int] = []
        for i, token in enumerate(best):
            if token != 0 and (i == 0 or token != best[i - 1]):
                out.append(int(token))
        return out

    a, b = decode(full), decode(small)
    same = a == b

    print(f"frames agreeing: {agree * 100:.2f}%")
    print(f"greedy decode: {len(a)} tokens against {len(b)}, {'identical' if same else 'DIFFERENT'}")

    if agree < 0.98:
        sys.exit("too many frames changed their mind; the alignment would move")

    # A limit of this check: noise is not singing. A speech model that hears noise gives blank on
    # every frame, so both models "agree" on silence, where they were never going to disagree. That
    # check cannot fail, and printing 100% next to it looks like evidence. Real evidence needs real
    # music aligned with the quantised file, with the words landing in the right places. This check
    # only catches a quantisation that breaks the graph completely.
    if not a:
        print("\n  NOTE: the decode is empty because noise is not speech, so the agreement above")
        print("  says only that neither model hallucinates words in static. It does NOT show that")
        print("  the alignment survived quantisation. For that, align real audio with both files.")


def main() -> None:
    # See the note in export_wav2vec2_onnx.py: the toolchain prints emoji, and a redirected stdout on
    # Windows uses cp1252, which would turn a working run into a UnicodeEncodeError at the end.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")

    root = pathlib.Path(__file__).resolve().parent.parent
    models = root / "RPM.Desktop" / "Assets" / "OnnxModels"

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=pathlib.Path, default=models / "wav2vec2.onnx")
    parser.add_argument("--out", type=pathlib.Path, default=models / "wav2vec2.int8.onnx")
    args = parser.parse_args()

    if not args.source.exists():
        sys.exit(f"{args.source} is not there — run scripts/export_wav2vec2_onnx.py first")

    quantise(args.source, args.out)
    check(args.source, args.out)


if __name__ == "__main__":
    main()
