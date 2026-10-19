#!/usr/bin/env python3
"""Writes an eight-bit copy of the exported Beat This! weights.

The beat model is the most expensive thing the app runs: a track is nine thirty-second windows, and
each window takes about half a second of processor time on a fast machine and more on an ordinary
x86 laptop. Dynamic quantisation stores the weight matrices as int8 and multiplies them as integers.
An x86 processor with VNNI does this several times faster than the float32 original, and an Apple
processor does it at about the same speed. The file shrinks from 79 MB to 22 MB in both cases.

Nothing is retrained and no data is needed: only the weights are quantised, and the activations are
scaled on each call from their own range. The accuracy cost is checked by this script. The two
models run over the recorded log-mel reference that the C# tests use, and are compared on the three
things the decoder reads: the sign of every frame, how well the two curves follow each other, and
where the peaks are.

Run it from the repo root with the venv that has onnxruntime in it. It reads
RPM.Desktop/Assets/OnnxModels/beat_this.onnx and writes beat_this.int8.onnx next to it.

    python3 scripts/quantize_beat_this.py

The quantised file is not shipped. Every build ships the float32 weights, because the shared
analysis pool needs every computer to read the same beat grid, and the U8S8 format written here can
saturate on an x86 processor without VNNI. The app opens the quantised file only when
RPM_BEAT_MODEL=int8 asks for it, for measurement.
"""

from __future__ import annotations

import argparse
import hashlib
import pathlib
import sys

import numpy as np
import onnxruntime as ort
from onnxruntime.quantization import QuantType, quantize_dynamic

REPO = pathlib.Path(__file__).resolve().parent.parent
MODELS = REPO / "RPM.Desktop" / "Assets" / "OnnxModels"
SOURCE = MODELS / "beat_this.onnx"
TARGET = MODELS / "beat_this.int8.onnx"

# The recorded spectrogram that the C# tests compare MelSpectrogram with, repeated to fill a full
# window. These are real numbers from a real signal: random noise would exercise the arithmetic but
# say nothing about the error on music, which is the error that matters.
REFERENCE = REPO / "RPM.Core.Tests" / "Assets" / "mel-reference.txt"
CHUNK_FRAMES = 1500

# What the comparison must reach. The limits are not from a standard: they are about ten times
# tighter than the error seen when this script was written. A future onnxruntime that quantises
# differently should fail here, not silently read worse beats.
MIN_CORRELATION = 0.999
MIN_SIGN_AGREEMENT = 0.998

# Only the weight matrices are quantised. This is where the time goes (62 of the model's 86 MatMuls
# have weights, and the rest are attention, which multiplies two computed values), and MatMul is the
# only op the quantiser has that every runtime can run.
#
# The four convolutions in the stem are left alone on purpose. Quantising them creates ConvInteger,
# and the runtime the app ships (1.20.1) has no CPU kernel for it. The failure comes at the first
# inference, not at load: "Could not find an implementation for ConvInteger(10)", and then the app
# reads no beats at all. They are four nodes out of several hundred, so quantising them gains almost
# nothing.
OP_TYPES = ["MatMul"]

# The check for that, independent of which onnxruntime made the file. Any integer op in the graph
# that is not on this list is an op that some runtime does not have.
ALLOWED_INTEGER_OPS = {"MatMulInteger", "DynamicQuantizeLinear"}


def window() -> np.ndarray:
    """One model input, built from the recorded reference."""
    mel = np.loadtxt(REFERENCE)
    tiled = np.tile(mel, (CHUNK_FRAMES // len(mel) + 1, 1))[:CHUNK_FRAMES]
    return tiled.astype(np.float32)[None, ...]


def read(model: pathlib.Path, spect: np.ndarray) -> dict[str, np.ndarray]:
    options = ort.SessionOptions()
    options.intra_op_num_threads = 2
    options.log_severity_level = 3
    session = ort.InferenceSession(str(model), options, providers=["CPUExecutionProvider"])
    name = session.get_inputs()[0].name
    outputs = session.run(None, {name: spect})
    return {o.name: v.ravel() for o, v in zip(session.get_outputs(), outputs)}


def peaks(values: np.ndarray) -> set[int]:
    """Where the decoder finds a beat: a local maximum above zero (even odds)."""
    return {
        i for i in range(1, len(values) - 1)
        if values[i] > 0 and values[i] >= values[i - 1] and values[i] >= values[i + 1]
    }


def ops_are_runnable(quantised: pathlib.Path) -> bool:
    """Whether the graph uses only integer ops that every runtime has a kernel for.

    This reads the file and does not run it, because a run here only proves that the onnxruntime in
    this venv can run it, and this script is normally run with a newer version than the app ships. A
    graph with a missing op loads without error and then throws on the first window.
    """
    import onnx

    graph = onnx.load(str(quantised)).graph
    integer_ops = {n.op_type for n in graph.node
                   if "Integer" in n.op_type or "Quantize" in n.op_type}
    unknown = integer_ops - ALLOWED_INTEGER_OPS
    if unknown:
        print(f"  graph uses {', '.join(sorted(unknown))}, which not every runtime implements",
              file=sys.stderr)
        return False

    print(f"  integer ops: {', '.join(sorted(integer_ops))}")
    return True


def compare(original: pathlib.Path, quantised: pathlib.Path) -> bool:
    spect = window()
    before, after = read(original, spect), read(quantised, spect)
    ok = True

    for name in before:
        a, b = before[name], after[name]
        correlation = float(np.corrcoef(a, b)[0, 1])
        agreement = float((np.sign(a) == np.sign(b)).mean())
        found, still = peaks(a), peaks(b)

        print(f"  {name:9s} correlation {correlation:.6f}  frames agreeing {agreement:.4f}  "
              f"peaks {len(found)} -> {len(still)}, {len(found & still)} in the same frame")

        if correlation < MIN_CORRELATION or agreement < MIN_SIGN_AGREEMENT:
            print(f"  {name}: the quantised model does not read the same beats", file=sys.stderr)
            ok = False
        if len(still) != len(found):
            print(f"  {name}: {len(found)} peaks became {len(still)}", file=sys.stderr)
            ok = False

    return ok


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--keep-on-failure", action="store_true",
                        help="write the file even if it reads different beats, to look at it")
    args = parser.parse_args()

    if not SOURCE.exists():
        print(f"no {SOURCE} — run scripts/fetch-models.sh first", file=sys.stderr)
        return 1

    print(f"quantising {SOURCE.name}...")
    quantize_dynamic(str(SOURCE), str(TARGET), weight_type=QuantType.QInt8,
                     per_channel=False, op_types_to_quantize=OP_TYPES)

    print(f"{SOURCE.stat().st_size / 1e6:.1f} MB -> {TARGET.stat().st_size / 1e6:.1f} MB")
    print("checking it against the original on the recorded reference:")

    kept = ops_are_runnable(TARGET) and compare(SOURCE, TARGET)
    if not kept and not args.keep_on_failure:
        TARGET.unlink()
        print("removed; the app keeps using the float32 weights", file=sys.stderr)
        return 1

    digest = hashlib.sha256(TARGET.read_bytes()).hexdigest()
    print(f"\n{TARGET}\nsha256 {digest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
