"""Writes the reference log-mel for a fixed signal, straight from the model's own preprocessing.

The signal is three sine tones plus two clicks. It can be built from a formula in any language, so
the C# test builds the same waveform and does not need to load one.
"""

import numpy as np
import torch

from beat_this.preprocessing import LogMelSpect

SR = 22050
n = SR  # one second

i = np.arange(n, dtype=np.float64)
x = (0.5 * np.sin(2 * np.pi * 440 * i / SR)
     + 0.3 * np.sin(2 * np.pi * 97 * i / SR)
     + 0.2 * np.sin(2 * np.pi * 3000 * i / SR))
x[5000] += 0.9
x[11000] += 0.9

mel = LogMelSpect()(torch.tensor(x, dtype=torch.float32)).numpy()
print("shape", mel.shape, "min", mel.min(), "max", mel.max())

out = ["# log-mel of the signal built in MelSpectrogramTests, from beat_this's own preprocessing.",
       "# One line per frame, one value per mel band.",
       f"# {mel.shape[0]} frames x {mel.shape[1]} bands"]
out += [" ".join(f"{v:.6f}" for v in row) for row in mel]
with open("RPM.Core.Tests/Assets/mel-reference.txt", "w") as f:
    f.write("\n".join(out) + "\n")
print("wrote RPM.Core.Tests/Assets/mel-reference.txt")
