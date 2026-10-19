"""Records what madmom's downbeat DBN makes of a set of curves, so the C# port can be tested against it.

BarDbn is a port of madmom's DBNDownBeatTrackingProcessor, called the way beat_this calls it. A port
of a Viterbi search can run, produce plausible bar lines, and still be wrong. Each of these details
gives a different answer if it is off by one:

  * the state space is one beat's states repeated for each beat of the bar, and the first beat of
    the bar is reached from its LAST beat, not from nothing;
  * the observation model has three outputs, and beat_this builds its first column as beat minus
    downbeat, not as beat;
  * transitions below numpy's spacing(1) are dropped BEFORE the row is normalised, which removes 884
    of 1764 of them, and the largest dropped one is still worth -36 in logs;
  * the initial distribution is log(1/n), and n differs between a bar of three and a bar of four, so
    that constant decides which bar length wins on a track the search finds ambiguous;
  * the activations are first trimmed to the loud part, and madmom's test for "is there any loud
    part" is false when the only loud frame is frame zero.

So the reference output decides. Each case below is a pair of curves built here, run through madmom
exactly as beat_this runs it, and written out with the beats, the bar lines and the bar length it
chose. BarDbnTests replays the same numbers and must reach the same answer.

    scripts/make_bar_dbn_reference.py > RPM.Core.Tests/Assets/bar-dbn-reference.txt

It needs madmom, which needs an old Python. This setup works:

    ~/.local/bin/python3.10 -m venv mm
    mm/bin/pip install "setuptools<81" wheel "numpy<1.24" cython
    mm/bin/pip install --no-build-isolation "madmom @ git+https://github.com/CPJKU/madmom.git"
"""

import numpy as np
from madmom.features.downbeats import DBNDownBeatTrackingProcessor

FPS = 50
MIN_BPM, MAX_BPM, TRANSITION_LAMBDA = 55.0, 215.0, 100
BEATS_PER_BAR = [3, 4]

# Logit values for "yes" and "no". They are not saturated on purpose: a curve of plus and minus
# infinity would give the same result whatever the observation model did with it.
ON, OFF = 4.0, -4.0

# The clipping constant beat_this applies between the model and the DBN, copied from its Postprocessor.postp_dbn.
EPSILON = 1e-5


def clicks(seconds, bpm, beats_per_bar=4, drop=(), double=(), drift=0.0,
           bar_strength=ON, first_bar=0, missing_bars=()):
    """Two curves: a peak on every beat, and a peak on every `beats_per_bar`-th beat.

    drop         -- beat numbers the model missed entirely.
    double       -- beat numbers the model marked twice, the second peak three frames later.
    drift        -- fraction the tempo speeds up over the whole excerpt.
    bar_strength -- the logit on a bar line, so a faint "one" can be tested.
    first_bar    -- which beat carries the first bar line.
    missing_bars -- bar lines the model did not mark at all.
    """
    frames = int(seconds * FPS)
    beat = np.full(frames, OFF, dtype=np.float64)
    bar = np.full(frames, OFF, dtype=np.float64)

    at, n, period, seen_bars = 0.0, 0, 60.0 * FPS / bpm, 0
    while at < frames:
        frame = int(round(at))
        if n not in drop:
            beat[frame] = ON
            if n in double and frame + 3 < frames:
                beat[frame + 3] = ON
            if n >= first_bar and (n - first_bar) % beats_per_bar == 0:
                if seen_bars not in missing_bars:
                    bar[frame] = bar_strength
                seen_bars += 1
        n += 1
        at += period * (1 - drift * at / frames)

    return beat, bar


def noise(seconds, seed):
    """Curves with no music in them. The search must still return something finite."""
    rng = np.random.default_rng(seed)
    return rng.normal(-2.0, 1.5, int(seconds * FPS)), rng.normal(-3.0, 1.5, int(seconds * FPS))


CASES = {
    # A plain four-to-the-floor track, and the same in three.
    "four_128": clicks(20, 128),
    "three_120": clicks(20, 120, beats_per_bar=3),
    # The model is least sure about the bar line, so a faint one is the case that matters: a peak
    # picker loses it, and the search must not.
    "four_128_faint_bars": clicks(20, 128, bar_strength=0.5),
    # Bar lines the model missed completely. Three are in a row, which is where a majority vote over
    # the marked ones starts to fail.
    "four_128_missing_bars": clicks(20, 128, missing_bars=(2, 3, 4, 9)),
    # The bar does not start on the first beat of the file.
    "four_140_offset_bar": clicks(20, 140, first_bar=2),
    # Beat faults that break a peak picker, here with bar lines as well.
    "four_128_doubled": clicks(20, 128, double=range(0, 100, 2)),
    "four_174_dropped": clicks(20, 174, drop=(7, 8, 9, 20, 21)),
    "four_128_drifting": clicks(20, 128, drift=0.08),
    # Slow enough to be at the top of the state space, where a truncating division loses a state.
    "four_60": clicks(20, 60),
    # Fast enough to be at the bottom of the state space.
    "four_200": clicks(20, 200),
    "noise": noise(20, 7),
}

dbn = DBNDownBeatTrackingProcessor(
    beats_per_bar=BEATS_PER_BAR, min_bpm=MIN_BPM, max_bpm=MAX_BPM, fps=FPS,
    transition_lambda=TRANSITION_LAMBDA)

print("# What madmom's DBNDownBeatTrackingProcessor made of each pair of curves, called the way")
print("# beat_this calls it. Replayed by BarDbnTests.")
print(f"# Generated by scripts/make_bar_dbn_reference.py against madmom, beats_per_bar={BEATS_PER_BAR} "
      f"min_bpm={MIN_BPM} max_bpm={MAX_BPM} fps={FPS} transition_lambda={TRANSITION_LAMBDA}.")
def fnv(values):
    value = 0xCBF29CE484222325
    for v in values:
        for byte in int(v).to_bytes(8, "little", signed=True):
            value = ((value ^ byte) * 0x100000001B3) & 0xFFFFFFFFFFFFFFFF
    return value


for beats, hmm in zip(BEATS_PER_BAR, dbn.hmms):
    st = hmm.transition_model.state_space
    om = hmm.observation_model
    tm = hmm.transition_model
    print(f"states {beats} {st.num_states}")

    # Which of the three densities each state reads. A port that is wrong by one state at one
    # interval still finds beats, but puts them in slightly wrong places on slow tracks.
    print(f"reads {beats} {fnv(om.pointers)}")

    # The transition model is recorded in two parts, because the two parts fail differently.
    #
    # Its shape is exact integers: which state may come before each state, in the order the search
    # considers them, which decides ties. This is also where the dropped transitions show: half of
    # them are below numpy's spacing(1) and are removed, not made small.
    #
    # Its weights are not exact and must not be compared as if they were. Python's logarithm and
    # .NET's differ in the last bit for a few of the 6861 weights, which is a difference of 1e-12 in
    # a number near -29. So the weights are recorded as their sum, which hardly moves for that
    # difference and moves a lot for any change to the formula.
    pairs, total = [], 0.0
    for dest in range(st.num_states):
        for k in range(tm.pointers[dest], tm.pointers[dest + 1]):
            pairs.append(int(dest))
            pairs.append(int(tm.states[k]))
            total += tm.log_probabilities[k]
    print(f"transitions {beats} {len(pairs) // 2} {fnv(pairs)} {total:.6f}")


def fingerprint(path):
    """FNV-1a hash of the path's states, so the whole path is checked without writing it all out.

    The beats alone do not fix the search result: two different paths can put the beats in the same
    places. A port that agrees on the beats but not on the path has a bug that will show on another
    track.
    """
    value = 0xCBF29CE484222325
    for state in path:
        for byte in int(state).to_bytes(4, "little"):
            value = ((value ^ byte) * 0x100000001B3) & 0xFFFFFFFFFFFFFFFF
    return value


def combined(beat_logits, bar_logits):
    """The activations beat_this gives the DBN: kept away from 0 and 1, then split three ways."""
    beat_prob = 1 / (1 + np.exp(-beat_logits))
    bar_prob = 1 / (1 + np.exp(-bar_logits))
    beat_prob = beat_prob * (1 - EPSILON) + EPSILON / 2
    bar_prob = bar_prob * (1 - EPSILON) + EPSILON / 2
    return np.vstack((np.maximum(beat_prob - bar_prob, EPSILON / 2), bar_prob)).T


def as_read(curve):
    """The curve as the C# side sees it: single precision, which is what the model outputs.

    Without this, the printing of the file causes the biggest differences. A curve of plus and minus
    four is written and read back exactly, but the noise case is not. If madmom used the full double
    while the test used the value that survived printing, the log probability would differ by about
    6e-4, which looks like a bug in the search but is not.
    """
    return np.asarray(curve, dtype=np.float32).astype(np.float64)


CASES = {name: (as_read(b), as_read(d)) for name, (b, d) in CASES.items()}


def written(values):
    """Nine significant figures, which a single-precision number needs to be read back exactly."""
    return " ".join(f"{v:.9g}" for v in values)


for name, (beat_logits, bar_logits) in CASES.items():
    act = combined(beat_logits, bar_logits)
    out = dbn(act)

    # Which bar length won, and the path it won with. They are calculated again here because the
    # processor does not keep them.
    trimmed = act
    from madmom.features.beats import threshold_activations
    if dbn.threshold:
        trimmed, _ = threshold_activations(act, dbn.threshold)
    scored = [hmm.viterbi(trimmed) for hmm in dbn.hmms]
    best = int(np.argmax([s[1] for s in scored]))

    print(f"case {name}")
    print("beat_logits " + written(beat_logits))
    print("bar_logits " + written(bar_logits))
    print(f"per_bar {BEATS_PER_BAR[best]}")
    print("beats " + " ".join(f"{b:.4f}" for b in out[:, 0]))
    print("bars " + " ".join(f"{b:.4f}" for b in out[out[:, 1] == 1][:, 0]))
    print(f"path {fingerprint(scored[best][0])}")
    print(f"logprob {' '.join(f'{s[1]:.9f}' for s in scored)}")
