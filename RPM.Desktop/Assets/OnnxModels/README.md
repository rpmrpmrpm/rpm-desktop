# ONNX models

`u2netp.onnx` and `ultraface.onnx` are committed, a few megabytes each. `beat_this.onnx`,
`skey.onnx`, `wav2vec2.int8.onnx` and `wav2vec2.vocab.json` are not: 214 megabytes the four. They are
listed as `RpmModel` items in
`RPM.Desktop.csproj`, each with its SHA-256, and the build's `FetchRpmModels` target downloads any
that is missing from `https://updates.rpmrpmrpm.com/models/<sha256>/<name>` and checks the hash
before moving it here. A clean checkout therefore builds, and the release workflow needs no step of
its own for them. A file that is here but has a different hash fails the build and is not replaced.
`scripts/fetch-models.sh` runs the same target without building, for the tools that read this folder
directly.

The beat model ships as the float32 `beat_this.onnx` only, on every platform. Beat grids go into a
pool that every user reads, and two reports agree only when they are the same text. On the same
window the float32 weights read alike on arm64 and x64 and the eight-bit copy did not. The server's
transcoder also reads the float32 file. `beat_this.int8.onnx` is kept
on the model host, at
`models/88112b293b47bcec1b6e508850f955edd746effeba2c5eb791cbc9deddb02695/beat_this.int8.onnx`, is not
fetched by the build, is kept out of the package by `RpmAssetsKeptOut`, and is opened only when
`RPM_BEAT_MODEL=int8` asks for it.

Lyric timing ships as `wav2vec2.int8.onnx` with its `wav2vec2.vocab.json`, on every platform. Both
files are fetched by the build, so every platform places lyrics word by word. The float32
`wav2vec2.onnx` is not on the model host and never ships (`RpmAssetsKeptOut`); a working copy that wants it, for the quantiser or LyricBench, exports it
with `scripts/export_wav2vec2_onnx.py`.

## Changing a model

Each URL is named by the file's hash and is never overwritten, so a changed model is a new upload
and a new hash, not a replacement:

1. Export the new file with its `scripts/export_*_onnx.py`.
2. A maintainer uploads it to the model host under its own hash.
3. Change `Sha256` on its `RpmModel` item in `RPM.Desktop.csproj`.
4. For `beat_this.onnx` only: the rpm server reads its own copy of this model, so a changed model
   has to be uploaded for the server too, or the server and the app read different weights. Say so
   in the pull request.

Keep the old upload. An installed app does not download models, but a build of an older commit
still asks for the hash that commit names.

## u2netp.onnx

U²-Netp is the small U²-Net, a salient-object detector. Given a photo it returns a per-pixel map of
what the picture is *about*: subject bright, background dark.

The art display uses it to decide which part of an artist backdrop survives being squeezed into a
wide, short strip (see `RPM.Core.Art.SaliencyCrop`). A hand-written edge and skin-tone
heuristic cannot tell a face from a brick wall, so a model does this work.

- **Upstream**: <https://github.com/xuebinqin/U-2-Net> (Qin et al., "U²-Net: Going Deeper with Nested
  U-Structure for Salient Object Detection", Pattern Recognition 2020)
- **Licence**: Apache 2.0
- **Size**: 4.6 MB. The full U²-Net is 176 MB and is not needed here, because the input is downscaled to
  320×320 before inference anyway.
- **IO**: input `[1, 3, 320, 320]` (RGB, normalised); the first output is the saliency map at
  `[1, 1, 320, 320]`, which is the only one used. The other six are the intermediate side outputs
  the training loss uses.

## ultraface.onnx

UltraFace (version-RFB-320) is a face detector. Given a photo it returns a rectangle per face.

U²-Netp says *what* the picture is about; this says *where the faces are*, which is the only thing
that can decide where a wide, short strip should cut a photo of a musician. Heuristics that infer
faces from a silhouette (skyline peaks, shoulder widths, eye-line ratios) each fail on some ordinary
photo.

- **Upstream**: <https://github.com/onnx/models> (Linzaer, Ultra-Light-Fast-Generic-Face-Detector-1MB)
- **Licence**: MIT
- **Size**: 1.2 MB
- **IO**: input `[1, 3, 240, 320]` (RGB, `(x - 127) / 128`); outputs `scores [1, N, 2]` and
  `boxes [1, N, 4]`, the boxes normalised `x1, y1, x2, y2`. Kept above 0.7 confidence, then
  non-maximum suppression at 0.4 IoU.

## beat_this.onnx

The float32 export: the file that ships, and the source of `beat_this.int8.onnx` below.

Beat This! is a transformer that reads beats and bar lines. Given a log-mel spectrogram it returns,
per 20 ms frame, how strongly it believes a beat and a bar line land there.

It takes the place of two readings that the DSP chain is least sure of. Autocorrelation over an onset
envelope finds a tempo but not which octave it is at (70 and 140 are the same curve), and a bar line
inferred from where the harmony moves is unreliable. Both decide how a mix is timed: the octave sets the length of
every fade, the bar phase decides whether two records meet on the one.

- **Upstream**: <https://github.com/CPJKU/beat_this> (Foscarin, Schlüter, Widmer, "Beat This! Accurate
  Beat Tracking Without DBN Postprocessing", ISMIR 2024), checkpoint `final0`
- **Licence**: MIT, for the code *and* the published weights
- **Size**: 79 MB, float32. Half-width weights would halve it, but the conversion breaks on the
  explicit casts in the rotary attention.
- **IO**: input `spect [1, 1500, 128]` (30 seconds of log-mel at 50 frames a second); outputs
  `beat [1, 1500]` and `downbeat [1, 1500]` as logits, above zero meaning better than even odds.
- **Rebuild**: `scripts/export_beat_this_onnx.py`, which checks the export against the original
  before keeping it.
- **Reproduced in C#**: the spectrogram (`RPM.Core.Analysis.MelSpectrogram`) and everything either
  side of the model (`RPM.Core.Analysis.BeatModelDecoder`) are ports of the reference preprocessing
  and postprocessing. Tests replay recorded reference output against both. A mistake
  in either returns worse beats rather than an error, so neither is left unchecked.

### What it costs

This is the only expensive thing the app runs. These numbers are for one window,
measured on an Apple M-series processor, `ORT_ENABLE_ALL`, one window at a time:

| threads | 1 | 2 | 4 | 8 |
| --- | --- | --- | --- | --- |
| per window | 644 ms | 516 ms | 497 ms | 705 ms |

Two threads is what `BeatModelService` asks for: four is barely better and takes cores the rest of
the app is using. A four minute track is nine windows (the chunks overlap by twelve frames), so
it takes around five seconds here and appreciably more on an ordinary x86 laptop, which is what the eight-bit
copy below is for.

Memory matters too: one window peaks near a gigabyte resident,
against 79 MB of weights on disk. `EnableMemoryPattern` is off for that reason. Planning one block
for every intermediate at once measured 1.40 GB against 0.97 GB with it off, and the difference in
time was within the noise. Running two windows at once measures 2.4 GB and four 3.5 GB,
which is why `BeatModelService` still lets exactly one through at a time even though a single
session is perfectly happy to be called from several threads.

### beat_this.int8.onnx

The same weights quantised to eight-bit integers by `scripts/quantize_beat_this.py`, which takes 79 MB
down to 22 MB. Only the weights are quantised; the activations are scaled per call from their own range, so nothing is
retrained and no calibration data is needed. `BeatModelChoice` prefers it when it is present, and
the script checks it against the float32 original on the recorded spectrogram before keeping it:
correlation 0.99998 on both outputs, agreement on the sign of every frame, and the same number of
peaks in both.

Only the `MatMul` nodes are quantised (62 of the model's 86), which is where the time goes. The four
convolutions in the stem are deliberately left alone: quantising them emits `ConvInteger`, which the
runtime the app ships has no CPU kernel for, and the failure is not at load but at the first window
(`Could not find an implementation for ConvInteger(10)`). The script refuses to keep a file with
any integer op outside `MatMulInteger` and `DynamicQuantizeLinear`. It checks the graph rather than
running it, because the onnxruntime that quantises is usually newer than the one that reads.

It does not ship. Its eight-bit format is U8S8 (`QuantType.QInt8` weights against
unsigned activations), which ONNX Runtime documents can saturate on an x86-64 processor with AVX2 or
AVX-512 and no VNNI, and the GiantSteps comparison below was run on Apple silicon only. On one window
its output differed between arm64 and x64 by about a thousandth, where the float32 weights differed
by about a millionth, and the shared pool counts two grids as agreeing only when they are identical.
`BeatModelChoice` opens it only when asked for by name, and never falls back from one file to the
other.

It is a large gain on an x86 processor with VNNI, where integer matrix multiplication is several
times faster, and about fifteen percent on an Apple one over a whole track (4.5 s against 5.2 over
GiantSteps Tempo, see `BeatModelChoice`).

Two environment variables pin the choice for a measurement, and neither has a fallback. If you ask for
something that is not there, the model does not load, because a run that quietly measured the other
file would be worse than no run:

- `RPM_BEAT_MODEL=int8` / `fp32`: which weights, instead of preferring the quantised ones.
- `RPM_ONNX_PROVIDER=dml`: run it on the graphics device through DirectML instead of the
  processor. Windows only, and only if the build has the DirectML runtime in it (see the RID
  condition in `RPM.Desktop.csproj`); it falls back to the processor and says so when it cannot
  start. Worth timing rather than assuming: a window wants about a gigabyte, and on a laptop whose
  graphics share the same memory that can go either way.

### What will not work

Feeding several windows at once. The batch axis is baked into the exported graph (a `Reshape` in
the stem asks for `{1, 1, 128, 1500}`), so freeing the input dimension only moves the failure into
the model. It would take a re-export with `dynamic_axes` on the batch, and then a decoder that asks
for windows in groups rather than one at a time. Both are real changes, not a session setting.

## skey.onnx

S-KEY is a key detector. Given a waveform it returns a probability for each of twenty-four keys.

It decides between major and minor. A key and its relative minor contain the same twelve notes,
so correlating a chromagram against key profiles cannot separate them reliably. Only which notes
the music leans on tells them apart. On the Camelot wheel the two sit at the same hour, so getting it
wrong scores a safe follow-on as a clash.

- **Upstream**: <https://github.com/deezer/skey> (Kong et al., S-KEY / STONE, ChromaNet over a
  harmonic VQT), the checkpoint bundled with the package
- **Licence**: MIT
- **Size**: 9.2 MB. This is larger than the 765 KB checkpoint because the constant-Q transform is itself a
  set of convolutions and is baked into the graph, which is why no transform has to be built in C#.
- **IO**: input `waveform [1, 1, 330750]` (15 seconds at 22050 Hz, mono, peak normalised); output
  `key_logits [1, 24]`, already softmaxed. Every axis is fixed, including the batch: the network
  reads its own normalisation shapes off the tensor, so nothing can be left free.
- **Output order**: twelve major keys from A, then twelve minor keys from B. This is not the order that anything
  else in the app counts in. Mapped by `RPM.Core.Analysis.KeyModelLabels` and pinned by tests, since
  a rotated table names every record a confident and wrong key.
- **Rebuild**: `scripts/export_skey_onnx.py`. Two substitutions are needed for the graph to trace at
  all, both explained in the script and both checked against an untouched copy of the model.

## wav2vec2.onnx

wav2vec2 is a speech model. The app reads it for one thing only: for every 20 ms of a record, which
letter it thinks is being sung.

Not transcription. The app already knows the words, because a lyric sheet has them, and wants only
to know **when** each was sung so the visualizer can follow the singer word by word instead of line
by line. That is forced alignment: `RPM.Core.Lyrics.CtcAligner` walks the likeliest path through
these frames that spells the words the sheet already gave. Recognition can be wrong about the words;
this cannot, because it is never asked. This is also why the base model is enough: nothing here
has to produce a transcript, only frame posteriors good enough to place letters.

Alone among the models here it is in no preflight manifest, and that is deliberate.
`RPM.Onnx.OnnxAligner` is built to find nothing: no weights, no runtime, or a window it will not
read, and the sheet keeps the line-level timing it already had. That timing is a guess, but it is a
working one, so a missing file costs a decoration rather than a feature and is not worth refusing to
open the app over.

- **Upstream**: <https://huggingface.co/facebook/wav2vec2-base-960h>
- **Licence**: Apache 2.0, so the exported file ships with the app
- **Size**: 360 MB, and 116 MB quantised. The quantised file and the vocabulary are `RpmModel` items,
  so every build downloads them; the float32 file is neither committed nor fetched.
- **IO**: input `audio [batch, samples]`, mono float32 at 16 kHz; output `logits [batch, frames, 32]`.
  Both lengths are dynamic, because the alignment window is one lyric line and a line is whatever
  length it is. Fixing either would mean padding every window to the longest line in the language
  and paying for the padding.
- **Frame rate**: 320 samples a frame, one per 20 ms. The app turns a frame index back into a moment
  of the record with it, so a drift here moves every word by a fixed factor and nothing else in the
  chain would notice. The export checks it.
- **Alphabet**: `wav2vec2.vocab.json`, written beside the weights rather than into the C# source.
  Every CTC model ships its own and they disagree about order, case and punctuation, so a table
  hardcoded in the app would be right for exactly these weights and quietly wrong for the next set.
  32 tokens, blank 0, word delimiter `|`. Without this file the aligner has no alphabet to spell in
  and stays unavailable whatever weights are present. See `CtcVocabulary`.
- **Rebuild**: `scripts/export_wav2vec2_onnx.py`, needing torch, transformers, onnx, onnxruntime and
  onnxscript. Opset 17 through the TorchScript exporter, not the dynamo one, which emits a graph the
  pinned onnxruntime 1.20.1 will not read. The script checks the export against the original on real
  noise before keeping it.
- **Quantise**: `scripts/quantize_wav2vec2.py`. Read its closing note before trusting the number it
  prints: noise is not singing, so a 100% agreement there says only that neither model hallucinates
  words in static. It catches a quantisation that broke the graph outright and nothing finer.
- **What ships**: the quantised file, on every platform. `OnnxAligner` opens `wav2vec2.int8.onnx`
  first and never falls back to `wav2vec2.onnx`, and `RpmAssetsKeptOut` keeps the 378 MB float32
  file out of every build.
- **On x86**: the file is U8S8, and on a processor with AVX2 and no VNNI it places words further from
  float32 than on Apple silicon. On one album (1,405 words), 6.8% of words are more than a second
  away from float32 against 3.9% on Apple silicon. Over 31 records by other artists (9,494 words)
  the figures are 2.8% against 2.5%. Every line is still placed. Quantising with `reduce_range` did
  not close that gap and made Apple silicon worse, so it stays off. A Ryzen 5 7530U (AVX2, no VNNI)
  placed every word exactly where Rosetta's AVX2 run did. Every other kernel that ONNX Runtime 1.20.1
  has for this file (AVX-VNNI, AVX-512 with and without VNNI, AMX) was measured on EC2 and lands at or
  closer to float32 than AVX2 does, so AVX2 without VNNI is the worst x86 case. A Linux machine with
  the same kernel places the same words as Windows. The app's x64 ffmpeg decodes FLAC about one float
  step off the arm64 one in most samples, so a PC aligns slightly different samples than a Mac. On
  those samples AVX2 gives 5.6% and 2.8%.
- **Measuring the AVX2 path from a Mac**: run an osx-x64 LyricBench with `ROSETTA_ADVERTISE_AVX=1`,
  and decode with the osx-x64 ffmpeg under it to get a PC's samples. Without the variable, Rosetta
  reports SSE4.1 only. Rosetta is not exact for every model (the beat model differs from that PC), so
  check on a PC when it matters.
- **DirectML**: a win-x64 publish of LyricBench takes the DirectML build of ONNX Runtime unless given
  `-p:RpmOnnxDirectMl=false`. On the processor the two place words the same.
