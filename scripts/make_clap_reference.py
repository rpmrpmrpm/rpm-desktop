"""Writes the reference log-mel for two fixed signals, straight from CLAP's own feature extractor.

The C# port is RPM.Core/Analysis/ClapFeatures.cs and the test is RPM.Core.Tests/ClapFeaturesTests.cs.
Each signal is three sine tones plus two clicks, built from a formula, so the C# test builds the same
waveform and does not need to load one:

- a full clip, ten seconds at 48 kHz, which the extractor reads as it is;
- a three-second clip, which the extractor repeats three times and pads with silence to ten seconds.

Every tenth frame is written (101 of the 1001), to keep the file small. A mistake in the window, the
filters or the decibel scale moves every frame, so a sample of them catches it.

Needs numpy and transformers (the version export_clap_onnx.py names), and the model's preprocessor
configuration, which transformers downloads on the first run.
"""

import numpy as np
from transformers import ClapFeatureExtractor

MODEL = "laion/larger_clap_music_and_speech"
SR = 48000
EVERY = 10


def signal(seconds):
    n = SR * seconds
    i = np.arange(n, dtype=np.float64)
    x = (0.5 * np.sin(2 * np.pi * 440 * i / SR)
         + 0.3 * np.sin(2 * np.pi * 97 * i / SR)
         + 0.2 * np.sin(2 * np.pi * 3000 * i / SR))
    x[n // 10] += 0.9
    x[n // 2] += 0.9
    return x.astype(np.float32)


def main():
    extractor = ClapFeatureExtractor.from_pretrained(MODEL)
    for seconds in (10, 3):
        mel = extractor(signal(seconds), sampling_rate=SR, return_tensors="np")["input_features"][0, 0]
        print(seconds, "s: shape", mel.shape, "min", mel.min(), "max", mel.max())
        rows = mel[::EVERY]
        out = [f"# CLAP log-mel of the {seconds}-second signal built in ClapFeaturesTests, from {MODEL}'s",
               "# own feature extractor (transformers ClapFeatureExtractor, rand_trunc, repeatpad).",
               f"# Every {EVERY}th frame of {mel.shape[0]}, one line per frame, one value per mel band."]
        out += [" ".join(f"{v:.4f}" for v in row) for row in rows]
        path = f"RPM.Core.Tests/Assets/clap-mel-reference-{seconds}s.txt"
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(out) + "\n")
        print("wrote", path)


if __name__ == "__main__":
    main()
