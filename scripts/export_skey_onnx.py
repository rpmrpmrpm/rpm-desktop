#!/usr/bin/env python3
"""Exports Deezer's S-KEY key detector to ONNX so the app can run it without Python.

S-KEY reads audio and returns one of twenty-four keys: twelve major and twelve minor. It replaces
template matching against a chroma vector, which finds the right notes but often confuses a major
key with its relative minor. The two share every note, and only the weight of the notes tells them
apart. A wrong answer moves a track to the other side of the Camelot wheel and makes a bad follow-on
track.

The constant-Q front end is itself a set of convolutions, so it is traced into the exported graph
together with the network. The app gives the file a waveform and gets key scores back, so no
transform has to be reimplemented in C#.

The weights are inside the pip package (765 KB, MIT licence like the code), so nothing has to be
downloaded at runtime.

Run it from the repo root with the venv that has torch and skey installed. It writes
RPM.Desktop/Assets/OnnxModels/skey.onnx and checks the exported file against the original before
keeping it.

    python3 scripts/export_skey_onnx.py
"""

from __future__ import annotations

import argparse
import pathlib
import sys

import numpy as np
import torch

from skey.key_detection import key_map, load_checkpoint, load_model_components

# The input the model was trained on, and so what the C# side must give it: mono, this rate, peak
# normalised to 1. The file is exported at the training excerpt length.
SAMPLE_RATE = 22050
EXCERPT_SECONDS = 15

DEFAULT_OUT = pathlib.Path("RPM.Desktop/Assets/OnnxModels/skey.onnx")


class SkeyOnnx(torch.nn.Module):
    """The three parts of S-KEY as one graph: transform, crop, network.

    The reference code keeps them apart because training shifts the crop to teach the model that a
    transposed piece is the same piece in another key. Inference never shifts it, so the crop is
    fixed at zero here and the three parts become a single call.
    """

    def __init__(self, hcqt: torch.nn.Module, chromanet: torch.nn.Module, crop: torch.nn.Module) -> None:
        super().__init__()
        self.hcqt = hcqt
        self.chromanet = chromanet
        self.crop = crop

    def forward(self, waveform: torch.Tensor) -> torch.Tensor:
        cropped = self.crop(self.hcqt(waveform), torch.zeros(1))
        return self.chromanet(cropped)


def freeze_norm_shapes(model: torch.nn.Module, example: torch.Tensor) -> int:
    """Fixes the layer normalisation shapes so the graph can be traced.

    The network normalises each block over ``x.shape[1:]``, which is read from the tensor as it
    passes through. The exporter cannot turn that into a graph: it sees a size being looked up, not
    a number, and stops.

    The sizes are constant once the input length is constant, so this runs the model once, records
    the shape each block received, and replaces the lookup with that number. The arithmetic does not
    change; the verify step checks this.
    """
    import torch.nn.functional as F

    from skey.convnext import ConvNeXtBlock, TimeDownsamplingBlock

    blocks = [m for m in model.modules() if isinstance(m, (ConvNeXtBlock, TimeDownsamplingBlock))]
    seen: dict[torch.nn.Module, tuple[int, ...]] = {}

    def note(module: torch.nn.Module, inputs: tuple[torch.Tensor, ...]) -> None:
        seen.setdefault(module, tuple(inputs[0].shape[1:]))
        # Returns nothing on purpose: a value returned by a pre-hook replaces the arguments.

    hooks = [block.register_forward_pre_hook(note) for block in blocks]
    with torch.no_grad():
        model(example)
    for hook in hooks:
        hook.remove()

    for block, shape in seen.items():
        block.norm = lambda x, _read_off_the_tensor, _shape=shape: F.layer_norm(x, _shape)
    return len(seen)


class TimeAveragePool(torch.nn.Module):
    """The average over time, written out directly.

    The network ends with an adaptive pool that asks for a 12x1 result. The twelve values are
    already there when the data reaches it (one per semitone, folded together an octave earlier), so
    the pool only averages the remaining axis, time. In this form it exports. As an adaptive pool it
    does not, because the exporter has no size to work back from.
    """

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return x.mean(dim=3, keepdim=True)


def replace_adaptive_pool(chromanet: torch.nn.Module, example_height: int = 12) -> None:
    """Replaces the adaptive pool with the plain average it is equal to here."""
    wanted = chromanet.global_average_pool.output_size
    if tuple(wanted) != (example_height, 1):
        sys.exit(f"unexpected pool size {tuple(wanted)}; the substitution below no longer holds")
    chromanet.global_average_pool = TimeAveragePool()


def export(out_path: pathlib.Path, opset: int) -> None:
    checkpoint = load_checkpoint()
    hcqt, chromanet, crop = load_model_components(checkpoint, torch.device("cpu"))
    wrapped = SkeyOnnx(hcqt, chromanet, crop).eval()

    # Noise, not silence: a constant-Q transform of silence is all zeros, so the trace would never
    # see the log of a real magnitude.
    example = torch.randn(1, 1, SAMPLE_RATE * EXCERPT_SECONDS) * 0.1

    print(f"pinned normalisation shapes in {freeze_norm_shapes(wrapped, example)} blocks")
    replace_adaptive_pool(chromanet)

    out_path.parent.mkdir(parents=True, exist_ok=True)
    with torch.no_grad():
        torch.onnx.export(
            wrapped,
            (example,),
            str(out_path),
            input_names=["waveform"],
            output_names=["key_logits"],
            # Every axis is fixed, including the batch. The network normalises each layer over the
            # shape read from the tensor itself, so any free axis becomes a node the exporter cannot
            # resolve. A track is covered by giving the file one 15 second window at a time and
            # averaging the scores.
            opset_version=opset,
            dynamo=False,
        )

    # Compare against a second, unpatched copy of the model, not the patched one. Otherwise a wrong
    # fixed shape would be checked against itself and would agree.
    reference = SkeyOnnx(*load_model_components(checkpoint, torch.device("cpu"))).eval()
    verify(reference, out_path, example)

    size_kb = out_path.stat().st_size / 1024
    print(f"wrote {out_path} ({size_kb:.0f} KB, {len(key_map)} keys)")


def verify(wrapped: torch.nn.Module, out_path: pathlib.Path, example: torch.Tensor) -> None:
    """Runs the original model and the exported file on the same input and compares the results.

    More than one window is checked, because a graph that folded a constant in from the traced
    window would still agree on that window.
    """
    import onnxruntime as ort

    session = ort.InferenceSession(str(out_path), providers=["CPUExecutionProvider"])

    worst = 0.0
    for name, audio in (
        ("traced window", example),
        ("fresh window", torch.randn(1, 1, SAMPLE_RATE * EXCERPT_SECONDS) * 0.1),
    ):
        with torch.no_grad():
            want = wrapped(audio).numpy()
        got = session.run(None, {"waveform": audio.numpy()})[0]
        drift = float(np.abs(want - got).max())
        worst = max(worst, drift)
        agree = (want.argmax(axis=-1) == got.argmax(axis=-1)).all()
        print(f"  {name}: largest drift {drift:.2e}, same key {agree}")

    if worst > 1e-3:
        out_path.unlink(missing_ok=True)
        sys.exit("export does not match the original model; file removed")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=pathlib.Path, default=DEFAULT_OUT)
    parser.add_argument("--opset", type=int, default=17)
    args = parser.parse_args()

    export(args.out, args.opset)


if __name__ == "__main__":
    main()
