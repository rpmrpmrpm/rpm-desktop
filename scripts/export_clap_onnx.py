#!/usr/bin/env python3
"""Exports the audio half of the CLAP model to ONNX, and the prompts' text embeddings to C#.

The model is laion/larger_clap_music_and_speech: Apache-2.0 weights, trained by LAION on
LAION-Audio-630K. The app uses it for the feeling lists (Melancholy, Happy, Angry, Romantic): the
audio half turns three ten-second clips of a record into one embedding, and the embedding is compared
with fixed text prompts. The prompts never change while the app runs, so their text embeddings are
worked out here, once, and the text half (125 M parameters, 499 MB) does not ship.

Run it from the repo root, in a Python environment with torch, transformers 4.57, onnx,
onnxruntime 1.20.1 (the version the app ships) and onnxconverter-common:

    python3 scripts/export_clap_onnx.py

It writes RPM.Desktop/Assets/OnnxModels/clap_audio.<weights>.onnx and
RPM.Core/Analysis/SoundPrompts.Data.cs, and prints the model file's SHA-256 for its RpmModel item in
RPM.Desktop.csproj. The export is checked against PyTorch before it is kept.

Two traps, both handled here:

- Use the TorchScript exporter at opset 17. The dynamo exporter writes graphs that onnxruntime 1.20.1
  does not always read.
- Take the PyTorch reference BEFORE exporting. Tracing for the export changes the model object, and
  the same object gives different answers afterwards (cosine about 0.1 with the right answer).
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import pathlib
import sys
import tempfile

import numpy as np
import torch

MODEL = "laion/larger_clap_music_and_speech"
SAMPLE_RATE = 48000
CLIP_SAMPLES = SAMPLE_RATE * 10

# The prompts the blind listening test used. Fixed before any result was seen. A change needs a new
# listening test, not only a new run of this script.
PROMPTS = {
    "Melancholy": ["sad melancholic music", "a sad, slow and emotional song",
                   "melancholy music about loss and longing"],
    "Happy": ["happy upbeat music", "a cheerful, joyful and feel-good song",
              "bright happy music that makes you smile"],
    "Angry": ["angry aggressive music", "a furious, intense and aggressive song", "music full of rage"],
    "Romantic": ["romantic love song", "a tender and intimate love song", "sensual romantic music for lovers"],
    "Zen": ["calm ambient new age music for meditation", "a peaceful ambient soundscape",
            "relaxing new age music"],
    "Festive": ["christmas music", "a festive holiday song with sleigh bells", "a christmas carol"],
}

# How close the shipped file must be to PyTorch, as the lowest cosine over the check clips. Eight-bit
# weights move an embedding a little; anything below these is a broken export, not rounding.
LEAST_COSINE = {"fp32": 0.9999, "fp16": 0.9999, "int8": 0.99}

# The only integer operators the eight-bit file may hold. The runtime the app ships has no CPU kernel
# for ConvInteger, and that failure shows at the first run, not at load.
INTEGER_OPS_ALLOWED = {"MatMulInteger", "DynamicQuantizeLinear"}

DEFAULT_DIR = pathlib.Path("RPM.Desktop/Assets/OnnxModels")
DATA_FILE = pathlib.Path("RPM.Core/Analysis/SoundPrompts.Data.cs")


def check_clips() -> list[np.ndarray]:
    """Three clips built from a formula: tones, clicks and noise, at full length and shorter."""
    rng = np.random.default_rng(20261117)
    clips = []
    for seconds, base in ((10, 220.0), (7, 330.0), (3, 440.0)):
        n = SAMPLE_RATE * seconds
        t = np.arange(n) / SAMPLE_RATE
        x = 0.4 * np.sin(2 * np.pi * base * t) + 0.2 * np.sin(2 * np.pi * base * 1.5 * t)
        x += 0.05 * rng.standard_normal(n)
        x[:: SAMPLE_RATE // 2] += 0.8
        clips.append(x.astype(np.float32))
    return clips


class AudioHalf(torch.nn.Module):
    """The audio tower and its projection, taking the mel features and returning one embedding per
    clip. is_longer is not an input: the model does not use it when feature fusion is off."""

    def __init__(self, model: torch.nn.Module) -> None:
        super().__init__()
        self.model = model

    def forward(self, input_features: torch.Tensor) -> torch.Tensor:
        out = self.model.get_audio_features(input_features=input_features)
        return getattr(out, "pooler_output", out)


def normalise(v: np.ndarray) -> np.ndarray:
    return v / np.maximum(np.linalg.norm(v, axis=-1, keepdims=True), 1e-12)


def pack_q15(v: np.ndarray) -> str:
    """The embedding as SoundEmbedding.Pack writes it: each number as a signed 16-bit fraction of one,
    little-endian, rounded half away from zero, then base64."""
    scaled = np.clip(v.astype(np.float64), -1.0, 1.0) * 32767.0
    rounded = np.sign(scaled) * np.floor(np.abs(scaled) + 0.5)
    return base64.b64encode(rounded.astype("<i2").tobytes()).decode("ascii")


def write_prompts(model, processor, path: pathlib.Path) -> None:
    rows = [(feeling, text) for feeling, texts in PROMPTS.items() for text in texts]
    inputs = processor(text=[text for _, text in rows], return_tensors="pt", padding=True)
    out = model.get_text_features(**inputs)
    vectors = normalise(getattr(out, "pooler_output", out).numpy())
    spread = (vectors @ vectors.T)[np.triu_indices(len(vectors), 1)].mean()
    if spread > 0.99:
        sys.exit(f"every prompt gives the same text embedding (mean cosine {spread:.3f}); the checkpoint is broken")

    lines = [
        "// <auto-generated>",
        "//     Written by scripts/export_clap_onnx.py from its PROMPTS table.",
        "//     Do not edit: every run overwrites this file. Change the script and run it again.",
        f"//     Text embeddings of the prompts by {MODEL} (Apache-2.0), each packed as",
        "//     SoundEmbedding.Pack packs an embedding, in base64.",
        "// </auto-generated>",
        "",
        "namespace RPM.Core.Analysis;",
        "",
        "public static partial class SoundPrompts",
        "{",
        "    // A method and not a field: static fields in two halves of a partial class are set in an",
        "    // order the language does not fix, and SoundPrompts.cs reads this while it sets its own.",
        "    private static (Feeling Feeling, string Text, string Packed)[] Data() =>",
        "    [",
    ]
    for (feeling, text), vector in zip(rows, vectors):
        lines.append(f'        (Feeling.{feeling}, "{text}",')
        lines.append(f'         "{pack_q15(vector)}"),')
    lines += ["    ];", "}", ""]
    path.write_text("\n".join(lines), encoding="utf-8", newline="\n")
    print(f"wrote {path} ({len(rows)} prompts)")


def make_weights(full: pathlib.Path, out_path: pathlib.Path, weights: str) -> None:
    import onnx

    if weights == "fp32":
        onnx.save(onnx.load(str(full)), str(out_path))
    elif weights == "fp16":
        from onnxconverter_common import float16

        onnx.save(float16.convert_float_to_float16(onnx.load(str(full)), keep_io_types=True), str(out_path))
    else:
        from onnxruntime.quantization import QuantType, quantize_dynamic

        # Only the MatMul weights, as scripts/quantize_beat_this.py does: that is where the time goes,
        # and it keeps every other operator in floating point.
        quantize_dynamic(str(full), str(out_path), weight_type=QuantType.QInt8, op_types_to_quantize=["MatMul"])
        ops = {node.op_type for node in onnx.load(str(out_path)).graph.node}
        stray = {op for op in ops if "Integer" in op or "Quantize" in op} - INTEGER_OPS_ALLOWED
        if stray:
            out_path.unlink()
            sys.exit(f"the eight-bit file holds operators the app's runtime cannot run: {sorted(stray)}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--weights", choices=["int8", "fp16", "fp32"], default="int8")
    parser.add_argument("--out-dir", type=pathlib.Path, default=DEFAULT_DIR)
    parser.add_argument("--data", type=pathlib.Path, default=DATA_FILE)
    args = parser.parse_args()

    import onnxruntime as ort
    from transformers import ClapModel, ClapProcessor

    torch.set_grad_enabled(False)
    model = ClapModel.from_pretrained(MODEL).eval()
    processor = ClapProcessor.from_pretrained(MODEL)

    # The prompts first, and the reference second: both before the export touches the model.
    write_prompts(model, processor, args.data)
    features = processor.feature_extractor(
        check_clips(), sampling_rate=SAMPLE_RATE, return_tensors="pt")["input_features"]
    reference = normalise(AudioHalf(model)(features).numpy())

    out_path = args.out_dir / f"clap_audio.{args.weights}.onnx"
    with tempfile.TemporaryDirectory() as scratch:
        full = pathlib.Path(scratch) / "clap_audio.fp32.onnx"
        torch.onnx.export(AudioHalf(model), (features,), str(full), input_names=["input_features"],
                          output_names=["embedding"], opset_version=17, dynamo=False,
                          dynamic_axes={"input_features": {0: "clips"}, "embedding": {0: "clips"}})
        make_weights(full, out_path, args.weights)

    session = ort.InferenceSession(str(out_path), providers=["CPUExecutionProvider"])
    exported = normalise(session.run(None, {"input_features": features.numpy()})[0])
    cosines = (reference * exported).sum(axis=1)
    print("cosine with PyTorch per check clip:", " ".join(f"{c:.6f}" for c in cosines))
    if cosines.min() < LEAST_COSINE[args.weights]:
        out_path.unlink()
        sys.exit(f"the export does not match PyTorch (lowest cosine {cosines.min():.6f}); nothing kept")

    digest = hashlib.sha256(out_path.read_bytes()).hexdigest()
    print(f"wrote {out_path}: {out_path.stat().st_size / 1e6:.1f} MB")
    print(f"sha256 {digest}")


if __name__ == "__main__":
    main()
