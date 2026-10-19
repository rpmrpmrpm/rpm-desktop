#!/usr/bin/env bash
# Fetches what tools/TempoBench measures against: two public tempo corpora and the reference
# annotations for them.
#
# The annotations come from tempo_eval (Schreiber, Urbano, Müller: "Music Tempo Estimation: Are We
# Done Yet?", TISMIR 2020). Its published results table, which our numbers can be compared with, is
# at https://tempoeval.github.io/tempo_eval_report/. The audio comes from the two servers that
# serve it. The download is about 2.5 GB in total, and none of it is committed.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
bench="$root/bench"
data="$bench/data"
mkdir -p "$data"

if [ ! -d "$bench/tempo_eval" ]; then
  echo "==> reference annotations (tempo_eval)"
  git clone --depth 1 https://github.com/tempoeval/tempo_eval.git "$bench/tempo_eval"
fi

# GTZAN: 1000 excerpts of 30 seconds over ten genres, the general-music benchmark. The tempi are
# Tzanetakis's version 1.0, which is what the published table uses.
if [ ! -d "$data/genres" ]; then
  echo "==> GTZAN audio (1.2 GB)"
  curl -L -o "$data/gtzan.tar.gz" \
    "https://huggingface.co/datasets/marsyas/gtzan/resolve/main/data/genres.tar.gz"
  tar xzf "$data/gtzan.tar.gz" -C "$data"
fi

# Ballroom: 698 excerpts of dance music from the ISMIR 2004 tempo contest. This is the corpus
# closest to the music RPM is made for.
if [ ! -d "$data/ballroom" ]; then
  echo "==> Ballroom audio (1.4 GB)"
  curl -L -o "$data/ballroom.tar.gz" \
    "https://mtg.upf.edu/ismir2004/contest/tempoContest/data1.tar.gz"
  mkdir -p "$data/ballroom"
  tar xzf "$data/ballroom.tar.gz" -C "$data/ballroom"
fi

# GiantSteps Tempo: 664 Beatport previews of two minutes, all electronic dance music, annotated from
# the corrections that real users sent to Beatport. It is the corpus that matters most to RPM. The
# corpus's own download script does not work, because Beatport's preview URLs are gone. JKU keeps a
# backup copy of the audio, so the files are fetched from there.
if [ ! -d "$data/giantsteps_tempo" ]; then
  echo "==> GiantSteps Tempo audio (930 MB)"
  mkdir -p "$data/giantsteps_tempo"
  ls "$bench/tempo_eval/annotations/references/giantsteps_tempo/jams" \
    | sed 's/\.jams$//' \
    | (cd "$data/giantsteps_tempo" && xargs -P 8 -I{} \
        curl -sfL --max-time 120 -o "{}.mp3" "https://www.cp.jku.at/datasets/giantsteps/backup/{}.mp3")
fi

cat <<EOF

Ready. To measure:

  dotnet run --project tools/TempoBench -c Release -- \\
    --corpus gtzan --audio bench/data/genres --refs bench/tempo_eval/annotations/references

  dotnet run --project tools/TempoBench -c Release -- \\
    --corpus ballroom --audio bench/data/ballroom --refs bench/tempo_eval/annotations/references

  dotnet run --project tools/TempoBench -c Release -- \\
    --corpus giantsteps_tempo --version 2.0 \\
    --audio bench/data/giantsteps_tempo --refs bench/tempo_eval/annotations/references
EOF
