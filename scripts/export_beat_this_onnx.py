#!/usr/bin/env python3
"""Exports the Beat This! beat tracker to ONNX so the app can run it without Python.

Beat This! (CPJKU, ISMIR 2024) reads a log-mel spectrogram and returns, per 20 ms frame, how
much it believes a beat and a downbeat land there. Both the code and the published weights are
MIT, so the exported file ships with the app.

The model is a transformer trained on 30 second excerpts, which is why the input length is fixed
at 1500 frames: a longer piece is cut into overlapping chunks and the results stitched back
together, exactly as the reference implementation does. A fixed shape also lets the runtime plan
its memory once instead of per call.

Run it from the repo root, in a Python environment that has torch and beat_this installed. It
writes RPM.Desktop/Assets/OnnxModels/beat_this.onnx and checks the exported file against the
original before it is kept.

    python3 scripts/export_beat_this_onnx.py
"""

from __future__ import annotations

import argparse
import pathlib
import sys

import numpy as np
import torch

from beat_this.inference import load_model

# What the model was trained on, and so what the C# side has to reproduce exactly when it builds
# the spectrogram. Kept here because the exported file cannot carry it.
SAMPLE_RATE = 22050
N_FFT = 1024
HOP_LENGTH = 441  # 20 ms per frame
N_MELS = 128
F_MIN, F_MAX = 30, 11000
LOG_MULTIPLIER = 1000

CHUNK_FRAMES = 1500  # 30 seconds, the length the model was trained at
BORDER_FRAMES = 6  # frames discarded at each chunk edge, where the model was never trained

DEFAULT_OUT = pathlib.Path("RPM.Desktop/Assets/OnnxModels/beat_this.onnx")


class BeatThisOnnx(torch.nn.Module):
    """The model with its dictionary output flattened.

    The tracker hands back ``{"beat": ..., "downbeat": ...}``. ONNX has no dictionaries, so this
    wrapper returns the same two tensors as a plain tuple, named on the way out.
    """

    def __init__(self, model: torch.nn.Module) -> None:
        super().__init__()
        self.model = model

    def forward(self, spect: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        out = self.model(spect)
        return out["beat"], out["downbeat"]


def halve(out_path: pathlib.Path) -> None:
    """Rewrites the model with float16 weights.

    The file is nearly 100 MB at full width, and all of it ships in the app. Half width halves
    that. A beat detector only has to decide which of two frames is more beat-like, so the lower
    precision should not change the answer. The verify step checks that this holds.
    """
    import onnx
    from onnxconverter_common import float16

    model = onnx.load(str(out_path))
    onnx.save(float16.convert_float_to_float16(model, keep_io_types=True), str(out_path))


def export(checkpoint: str, out_path: pathlib.Path, opset: int, fp16: bool = False) -> None:
    model = load_model(checkpoint, device="cpu")
    wrapped = BeatThisOnnx(model).eval()

    # Values in the range a real log-mel lands in, so the export traces the same branches a real
    # call would. Shape is (batch, time, mel).
    example = torch.rand(1, CHUNK_FRAMES, N_MELS) * 6

    out_path.parent.mkdir(parents=True, exist_ok=True)
    with torch.no_grad():
        torch.onnx.export(
            wrapped,
            (example,),
            str(out_path),
            input_names=["spect"],
            output_names=["beat", "downbeat"],
            opset_version=opset,
            dynamo=False,
        )

    if fp16:
        halve(out_path)

    verify(wrapped, out_path, example, tolerance=0.05 if fp16 else 1e-3)

    size_mb = out_path.stat().st_size / (1024 * 1024)
    print(f"wrote {out_path} ({size_mb:.1f} MB)")


def verify(
    wrapped: torch.nn.Module,
    out_path: pathlib.Path,
    example: torch.Tensor,
    tolerance: float,
) -> None:
    """Runs both the original and the exported file on the same input and compares.

    An export that loads but drifts is worse than one that fails, because the drift only shows up
    later as beats that are placed wrongly.
    """
    import onnxruntime as ort

    with torch.no_grad():
        want_beat, want_downbeat = wrapped(example)

    session = ort.InferenceSession(str(out_path), providers=["CPUExecutionProvider"])
    got_beat, got_downbeat = session.run(None, {"spect": example.numpy()})

    beat_drift = float(np.abs(want_beat.numpy() - got_beat).max())
    downbeat_drift = float(np.abs(want_downbeat.numpy() - got_downbeat).max())
    print(f"largest drift from the original: beat {beat_drift:.2e}, downbeat {downbeat_drift:.2e}")

    # The outputs are logits, not probabilities. The tolerance is above the rounding error that
    # float32 arithmetic gives when the runtime reorders it. A larger drift is a broken export.
    if max(beat_drift, downbeat_drift) > tolerance:
        out_path.unlink(missing_ok=True)
        sys.exit("export does not match the original model; file removed")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--checkpoint", default="final0", help="Beat This! checkpoint name or path")
    parser.add_argument("--out", type=pathlib.Path, default=DEFAULT_OUT)
    parser.add_argument("--opset", type=int, default=17)
    parser.add_argument("--fp16", action="store_true", help="half-width weights, half the file")
    args = parser.parse_args()

    export(args.checkpoint, args.out, args.opset, args.fp16)


if __name__ == "__main__":
    main()
