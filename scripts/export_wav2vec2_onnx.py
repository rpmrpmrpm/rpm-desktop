#!/usr/bin/env python3
"""Exports wav2vec2-base-960h to ONNX so the app can align lyrics without Python.

The model is not used for transcription. The app already has the words (from the lyric sheet) and
only needs to know WHEN each word was sung, so that the visualizer can follow the singer word by
word instead of line by line. This is forced alignment: the model gives the probability of each
letter in every 20 ms frame, and RPM.Core.Lyrics.CtcAligner finds the most likely path through those
frames that spells the words of the sheet. The model is never asked for the words, so it cannot get
them wrong.

For the same reason a small model is enough. It only has to give frame probabilities that are good
enough to place letters, and the base model does that for four seconds of audio in much less time
than the gap between two lyric lines. facebook/wav2vec2-base-960h is Apache 2.0, so the exported
file ships with the app.

The alignment window is one lyric line, not a whole track, so the input length is dynamic. The front
of the model is convolutional and the rest is a transformer, and both accept any length.

Needs torch, transformers, onnx, onnxruntime and onnxscript. Recent torch versions route
torch.onnx.export through code that needs onnxscript and will not start without it.

Run it from the repo root with a Python environment that has these packages. It writes
RPM.Desktop/Assets/OnnxModels/wav2vec2.onnx and wav2vec2.vocab.json, and checks the exported file
against the original before it keeps either.

    python3 scripts/export_wav2vec2_onnx.py

Then scripts/quantize_wav2vec2.py makes the eight-bit copy the app prefers.
"""

from __future__ import annotations

import argparse
import json
import pathlib
import sys

import numpy as np
import torch

MODEL = "facebook/wav2vec2-base-960h"

# The sample rate the model was trained on, so the C# side must resample to it. The exported file
# cannot store this, and the resampling (done with ffmpeg) must match it exactly.
SAMPLE_RATE = 16000

# The stride of the convolutional front end, in samples: 320 at 16 kHz, so one frame per 20 ms.
# The app needs this to turn a frame index back into a time in the track.
SAMPLES_PER_FRAME = 320

# The length of the dummy input used to trace the graph. The exported axes are dynamic, so nothing
# depends on it at run time, but a length near a real lyric line gives realistic traced shapes.
TRACE_SECONDS = 4


def export(destination: pathlib.Path, opset: int, source: str = MODEL) -> None:
    from transformers import Wav2Vec2ForCTC, Wav2Vec2Processor

    print(f"loading {source} …")
    model = Wav2Vec2ForCTC.from_pretrained(source)
    model.eval()

    processor = Wav2Vec2Processor.from_pretrained(source)

    # The alphabet.
    #
    # Written next to the weights, not into the C# source. Each CTC model has its own alphabet, with
    # a different order, case and punctuation, so a table fixed in the app would be right for these
    # weights only and silently wrong for other weights. See CtcVocabulary.
    vocab = processor.tokenizer.get_vocab()
    alphabet = {
        "model": str(source),
        "sample_rate": SAMPLE_RATE,
        "samples_per_frame": SAMPLES_PER_FRAME,
        "blank": processor.tokenizer.pad_token_id,
        "delimiter": processor.tokenizer.word_delimiter_token,
        "tokens": {token: int(i) for token, i in sorted(vocab.items(), key=lambda kv: kv[1])},
    }

    audio = torch.zeros(1, SAMPLE_RATE * TRACE_SECONDS, dtype=torch.float32)

    onnx_path = destination / "wav2vec2.onnx"
    print(f"exporting to {onnx_path} …")
    torch.onnx.export(
        model,
        (audio,),
        str(onnx_path),
        input_names=["audio"],
        output_names=["logits"],
        # Lyric lines have different lengths, and so do the outputs. Fixed sizes would mean padding
        # every window to the longest possible line and spending time on the padding.
        dynamic_axes={
            "audio": {0: "batch", 1: "samples"},
            "logits": {0: "batch", 1: "frames"},
        },
        opset_version=opset,
        do_constant_folding=True,
        # Use the TorchScript exporter, not the dynamo one (the default from torch 2.13). The dynamo
        # exporter writes a graph at its own opset and then converts it down, which fails on this
        # model: the converter cannot turn the axes input of Squeeze back into an attribute. Also,
        # the app pins onnxruntime 1.20.1, which may not load a graph at the opset that dynamo
        # prefers. Opset 17 through the TorchScript path is a common export for this model and is
        # certain to load in 1.20.1.
        dynamo=False,
    )

    check(model, onnx_path, audio)

    vocab_path = destination / "wav2vec2.vocab.json"
    vocab_path.write_text(json.dumps(alphabet, indent=2), encoding="utf-8")
    print(f"wrote {vocab_path} ({len(alphabet['tokens'])} tokens, "
          f"blank={alphabet['blank']}, delimiter={alphabet['delimiter']!r})")

    size = onnx_path.stat().st_size / (1024 * 1024)
    print(f"wrote {onnx_path} ({size:.0f} MB)")


def check(model, onnx_path: pathlib.Path, audio: torch.Tensor) -> None:
    """Compares the exported graph with the original model, using random noise instead of the zeros
    used for tracing. A traced model can be wrong without any error: a control-flow branch fixed at
    the wrong value gives a file that loads, runs and returns a plausible result. So this check is
    required."""
    import onnxruntime as ort

    torch.manual_seed(0)
    noise = torch.randn_like(audio) * 0.1

    with torch.no_grad():
        expected = model(noise).logits.numpy()

    session = ort.InferenceSession(str(onnx_path), providers=["CPUExecutionProvider"])
    got = session.run(["logits"], {"audio": noise.numpy()})[0]

    if got.shape != expected.shape:
        sys.exit(f"shape changed on export: {expected.shape} became {got.shape}")

    worst = float(np.abs(got - expected).max())
    print(f"checked against the original: worst frame differs by {worst:.2e}")
    if worst > 1e-3:
        sys.exit("the exported model does not agree with the original")

    # Check that the frame rate is the one the app assumes. If it differs, every word is placed at the
    # wrong time by a fixed factor, and nothing else in the chain would notice.
    frames = got.shape[1]
    implied = noise.shape[1] / frames
    print(f"{frames} frames for {noise.shape[1]} samples: {implied:.1f} samples a frame")
    if abs(implied - SAMPLES_PER_FRAME) > SAMPLES_PER_FRAME * 0.02:
        sys.exit(f"expected about {SAMPLES_PER_FRAME} samples a frame, got {implied:.1f}")


def main() -> None:
    # The exporter prints emoji in its progress messages. A Windows console, or any redirected stdout
    # on Windows, uses cp1252, which cannot encode them, so the script would stop with an encoding
    # error after the graph is already exported.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")

    root = pathlib.Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--out",
        type=pathlib.Path,
        default=root / "RPM.Desktop" / "Assets" / "OnnxModels",
        help="where the weights go (default: the app's own model folder)",
    )
    parser.add_argument(
        "--model",
        default=MODEL,
        help="what to export: the upstream weights by default, or a folder holding an adapted "
             "checkpoint — see scripts/finetune_wav2vec2_singing.py. The vocabulary is the same "
             "either way, because the app's own CtcVocabulary reads it.",
    )
    parser.add_argument("--opset", type=int, default=17)
    args = parser.parse_args()

    args.out.mkdir(parents=True, exist_ok=True)
    export(args.out, args.opset, args.model)


if __name__ == "__main__":
    main()
