#!/usr/bin/env python3
"""Adapts wav2vec2-base-960h to singing, using a corpus cut from a record collection.

The aligner's main limit is that its weights have never heard singing. The 960h training set is
read speech: evenly paced, one voice, no band. Errors on a record happen where singing differs from
speech: a vowel held over a chord, two voices in harmony, a syllable stretched across a bar. The
model fails there as an out-of-distribution model usually does: it is confident but vague. On one
test record the chorus scores -6.56 and the narrative line after it scores -3.51, with the same
voice and the same mix.

Separating the vocal does not help. Against a real studio acapella, an isolated vocal scored no
better than the full mix (see tools/LyricBench/README.md). The band is not the problem. The singing
is.

WHAT IT TRAINS ON. Not DALI: its annotations are access-gated, its audio is not distributed, and
its word times were made by machine. A collection with synced lyric sheets has none of those
problems, and it is the same kind of music that the app plays. tools/LyricCorpus cuts it into one
clip per lyric line, over the same window that the aligner is given at inference.

CTC does not need word times. It is given a clip and the words sung over it, and it works out the
alignment itself. So an LRC file that stamps only where each line STARTS is enough to train on.

WHAT IS LEFT UNCHANGED. The vocabulary (all 32 tokens), because the exported asset has to drop into
an app whose CtcVocabulary reads that exact file. The CTC head is kept and adapted, not
reinitialised: the letters are the same, and a new head would throw away 960 hours of training to
relearn the alphabet from about twenty. The convolutional front end is frozen. This is the standard
recipe: the front end reads the waveform, and twenty hours is not enough to teach it a better way
to do that without losing the old one.

    python3 scripts/finetune_wav2vec2_singing.py --corpus E:/rpm-lyric-corpus --out weights/singing

Then, to make the asset the app ships:

    python3 scripts/export_wav2vec2_onnx.py --model weights/singing --out RPM.Desktop/Assets/OnnxModels
    python3 scripts/quantize_wav2vec2.py

Needs torch with a working GPU, transformers, datasets, soundfile and jiwer. Training on a CPU
would take far too long.
"""

from __future__ import annotations

import argparse
import csv
import pathlib
import random
import re
import sys

MODEL = "facebook/wav2vec2-base-960h"

SAMPLE_RATE = 16000

# The convolutional stride, in samples: one frame per 20 ms. A transcript must not be longer than
# the number of frames. If it is, CTC has no valid path and the loss becomes infinity instead of
# raising an error.
SAMPLES_PER_FRAME = 320

# Everything the shipped alphabet can spell. Anything else becomes a space: a lyric sheet has many
# brackets, ellipses and typographic characters that the model has no token for. If they stayed in,
# the target would contain symbols that the model cannot produce.
KEEPS = re.compile(r"[^A-Z' ]+")


def spoken(text: str) -> str:
    """Returns one line as the model must spell it: capitals, letters and apostrophes, single spaces."""
    return " ".join(KEEPS.sub(" ", text.upper()).split())


def read_corpus(root: pathlib.Path) -> list[dict]:
    """Returns the clips and their words, as tools/LyricCorpus wrote them."""
    rows: list[dict] = []
    with (root / "corpus.tsv").open(encoding="utf-8", newline="") as file:
        for row in csv.DictReader(file, delimiter="\t"):
            words = spoken(row["text"])
            seconds = float(row["seconds"])
            if not words:
                continue

            # A clip that is too short for its own transcript is an impossible example, and CTC
            # returns an infinite loss for it.
            if len(words) > (seconds * SAMPLE_RATE) / SAMPLES_PER_FRAME - 1:
                continue

            rows.append({"path": str(root / row["clip"]), "text": words, "seconds": seconds})

    return rows


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--corpus", type=pathlib.Path, required=True,
                        help="the folder tools/LyricCorpus wrote: clips/ and corpus.tsv")
    parser.add_argument("--out", type=pathlib.Path, required=True,
                        help="where the adapted checkpoint goes")
    parser.add_argument("--epochs", type=float, default=3.0)
    # Low on purpose. This adapts a trained model on a small corpus. At 1e-4 the model loses its
    # English in the first epoch and does not recover it.
    parser.add_argument("--lr", type=float, default=2e-5)
    parser.add_argument("--batch", type=int, default=8)
    parser.add_argument("--holdout", type=int, default=400,
                        help="clips kept back to score with, taken by record so nothing leaks")
    args = parser.parse_args()

    import numpy as np
    import soundfile as sf
    import torch
    from torch.utils.data import Dataset
    from transformers import (Trainer, TrainingArguments, Wav2Vec2ForCTC, Wav2Vec2Processor)

    if not torch.cuda.is_available():
        print("no GPU: this needs one. A twenty hour corpus is days of processor time.", file=sys.stderr)
        sys.exit(1)

    rows = read_corpus(args.corpus)
    if not rows:
        print(f"nothing to train on under {args.corpus}", file=sys.stderr)
        sys.exit(1)

    # Split by CLIP NAME PREFIX, which identifies the record: clips of one song share a hash. A
    # holdout that shares a record with the training set would already have heard the singer.
    records = sorted({pathlib.Path(row["path"]).name.split("-")[0] for row in rows})
    random.Random(20260820).shuffle(records)

    held: set[str] = set()
    count = 0
    for record in records:
        if count >= args.holdout:
            break
        held.add(record)
        count += sum(1 for row in rows if pathlib.Path(row["path"]).name.startswith(record))

    train = [r for r in rows if pathlib.Path(r["path"]).name.split("-")[0] not in held]
    score = [r for r in rows if pathlib.Path(r["path"]).name.split("-")[0] in held]

    hours = sum(r["seconds"] for r in train) / 3600
    print(f"{len(train)} clips ({hours:.1f}h) to train on, {len(score)} to score with, "
          f"{len(records) - len(held)} records against {len(held)}")

    processor = Wav2Vec2Processor.from_pretrained(MODEL)

    class Clips(Dataset):
        def __init__(self, rows: list[dict]) -> None:
            self.rows = rows

        def __len__(self) -> int:
            return len(self.rows)

        def __getitem__(self, i: int) -> dict:
            row = self.rows[i]
            wave, rate = sf.read(row["path"], dtype="float32")
            assert rate == SAMPLE_RATE, f"{row['path']} is {rate} Hz"
            return {
                "input_values": processor(wave, sampling_rate=rate).input_values[0],
                "labels": processor.tokenizer(row["text"]).input_ids,
            }

    def collate(batch: list[dict]) -> dict:
        audio = processor.pad([{"input_values": b["input_values"]} for b in batch], return_tensors="pt")
        text = processor.tokenizer.pad([{"input_ids": b["labels"]} for b in batch], return_tensors="pt")
        # The pad is not a letter and must not be learned as one. The loss ignores -100.
        audio["labels"] = text["input_ids"].masked_fill(text.attention_mask.ne(1), -100)
        return audio

    model = Wav2Vec2ForCTC.from_pretrained(
        MODEL,
        ctc_loss_reduction="mean",
        pad_token_id=processor.tokenizer.pad_token_id,
    )
    model.freeze_feature_encoder()

    import jiwer

    def score_batch(prediction) -> dict:
        logits, labels = prediction
        said = processor.batch_decode(np.argmax(logits, axis=-1))
        labels = np.where(labels != -100, labels, processor.tokenizer.pad_token_id)
        meant = processor.batch_decode(labels, group_tokens=False)
        pairs = [(m, s) for m, s in zip(meant, said) if m.strip()]
        if not pairs:
            return {"wer": 1.0}
        return {"wer": jiwer.wer([m for m, _ in pairs], [s for _, s in pairs])}

    trainer = Trainer(
        model=model,
        args=TrainingArguments(
            output_dir=str(args.out / "runs"),
            per_device_train_batch_size=args.batch,
            per_device_eval_batch_size=args.batch,
            gradient_accumulation_steps=2,
            num_train_epochs=args.epochs,
            learning_rate=args.lr,
            warmup_ratio=0.1,
            fp16=True,
            group_by_length=True,
            length_column_name="seconds",
            eval_strategy="epoch",
            save_strategy="epoch",
            logging_steps=50,
            save_total_limit=2,
            report_to=[],
        ),
        train_dataset=Clips(train),
        eval_dataset=Clips(score),
        data_collator=collate,
        compute_metrics=score_batch,
    )

    # The score before any training step, so that the run can be read as a change. A word error rate
    # on singing is not accuracy, because the model is not used to transcribe. It does move when the
    # acoustic model improves, and it is the only cheap signal during training. The reliable
    # measurement comes afterwards from tools/LyricBench on real records.
    print("before:", trainer.evaluate())
    trainer.train()
    print("after:", trainer.evaluate())

    args.out.mkdir(parents=True, exist_ok=True)
    model.save_pretrained(args.out)
    processor.save_pretrained(args.out)
    print(f"wrote {args.out}. Export it with scripts/export_wav2vec2_onnx.py --model {args.out}")


if __name__ == "__main__":
    main()
