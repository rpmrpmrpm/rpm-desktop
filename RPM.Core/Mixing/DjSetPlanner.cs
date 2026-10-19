using RPM.Core.Analysis;

namespace RPM.Core.Mixing;

/// <summary>
/// Picks what comes next. Given the record on the deck and the crate of records not yet played, it
/// scores each candidate on three things that decide whether a mix works: tempo, key and energy. It
/// takes the best one.
///
/// <para>Tempo has the most weight, because it is the one thing that cannot be adjusted by much. Two
/// records more than a few percent apart cannot be beat-matched at all. Key comes next, on the Camelot
/// wheel. Energy comes last and has a small weight. It stops a set from jumping from a very loud record
/// straight into a ballad. It does not sort the whole set by loudness.</para>
/// </summary>
public static class DjSetPlanner
{
    /// <summary>The weight of each part. They sum to 1, so a score can be read as "how good a follow-on
    /// this is, from 0 to 1".</summary>
    private const double TempoWeight = 0.5, KeyWeight = 0.3, EnergyWeight = 0.2;

    /// <summary>Tempo difference, as a share of the playing tempo, at which the score reaches zero.
    /// This is roughly where the pull runs out. Two decks, each bent by up to
    /// <see cref="TransitionPlanner.MaxTempoPull"/>, can meet a pair within a factor of 1.1 or 0.9. For
    /// example, 128 BPM meets 104 to 158. Past that range only a blend is possible.</summary>
    private const double TempoSpan = 0.20;


    /// <summary>
    /// What a model's reading adds to the score, on top of the bonus for a record that has been
    /// analysed.
    ///
    /// <para>It is added, not substituted. A machine with no model weights scores exactly as it did
    /// before: every record it can read is analysed, none is modelled, and this tier never applies.
    /// When both kinds are present, for example part way through upgrading a set, this favours the
    /// records that have been read again with the model.</para>
    ///
    /// <para>It is needed because nothing else in this score can see the difference.
    /// <see cref="TempoScore"/> compares at half and double on purpose. So a DSP reading that is an
    /// octave out follows as well as a correct one. The octave only shows later, in the tempo pull and
    /// the length of the fade, and there it has a cost. Preferring the reading that got the octave from
    /// a model is the one place where this choice can be made before it costs anything.</para>
    ///
    /// <para>It is small on purpose. It should decide between two records that would both work. It must
    /// never put a bad follow-on in front of a good one.</para>
    /// </summary>
    private const double ModelledBonus = 0.03;

    /// <summary>
    /// How well <paramref name="candidate"/> would follow a record playing at
    /// <paramref name="playingBpm"/> in <paramref name="playingKey"/> at
    /// <paramref name="playingIntensity"/>. The result is from 0 to 1, and higher is better.
    /// </summary>
    public static double Score(MixTrack candidate, double playingBpm, MusicalKey playingKey, double playingIntensity)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        double tempo = TempoScore(playingBpm, candidate.Bpm);
        double key = KeyScore(playingKey, candidate.Key);
        double energy = EnergyScore(playingIntensity, candidate.Intensity);

        double score = TempoWeight * tempo + KeyWeight * key + EnergyWeight * energy;
        if (candidate.IsModelled)
            score += ModelledBonus;
        return Math.Clamp(score, 0, 1);
    }

    /// <summary>Unknown tempos score in the middle. "No idea" must not beat a good match, and it must
    /// not lose to a bad one.</summary>
    private static double TempoScore(double playingBpm, double candidateBpm)
    {
        if (playingBpm <= 0 || candidateBpm <= 0)
            return 0.5;

        // Compare on the folded tempo and also at half and double. A 140 BPM record follows a 70 BPM
        // one perfectly well. The fold in the estimator may have put the two an octave apart.
        double best = 1;
        foreach (double multiple in (ReadOnlySpan<double>)[0.5, 1.0, 2.0])
        {
            double difference = Math.Abs(candidateBpm * multiple - playingBpm) / playingBpm;
            best = Math.Min(best, difference);
        }
        return Math.Clamp(1 - best / TempoSpan, 0, 1);
    }

    /// <summary>Camelot adjacency, softened. The same key scores 1, a neighbour 0.8, and two steps
    /// 0.45. A clash still scores something, because a set that runs out of harmonic matches must carry
    /// on rather than stop.</summary>
    private static double KeyScore(MusicalKey playing, MusicalKey candidate)
    {
        int distance = playing.Distance(candidate);
        return distance switch
        {
            0 => 1.0,
            1 => 0.8,
            2 => 0.45,
            3 => 0.25,
            int.MaxValue => 0.5,      // one of them has no key, so there is no evidence either way
            _ => 0.1,
        };
    }

    /// <summary>A small step up in energy is ideal, and a big drop is not. The curve is shallow on
    /// purpose. It shapes the set but does not sort it.</summary>
    private static double EnergyScore(double playing, double candidate)
    {
        double step = candidate - playing;
        // Best at +0.05, falling away on either side. A drop of 0.4 still scores about 0.1.
        double distance = Math.Abs(step - 0.05);
        return Math.Clamp(1 - distance * 2, 0, 1);
    }

    /// <summary>
    /// The best follow-on from <paramref name="candidates"/>, or null when there are none. Ties go to
    /// the earlier candidate, so a set with no analysis at all comes out in playlist order.
    ///
    /// <para><b>A record that has been read wins over one that has not, whatever they score.</b> The
    /// two cannot be compared. Everything scored here (tempo, key, energy) is unknown for an unread
    /// record. An unknown scores in the middle on purpose, so an unread record could beat a real record
    /// that only follows imperfectly. Taking an unread record costs the changeover. Without a grid
    /// there is no bar line to land the incoming record on, so the mix becomes a fade on the button
    /// press. So unread records are set aside while any read record is left. Only a crate with nothing
    /// read in it is scored on what little is known. That is the first minute of a playlist that has
    /// never been played, and it ends as soon as the first reading arrives.</para>
    /// </summary>
    /// <param name="playingIntensity">How hard the playing record is hitting right now, when the caller
    /// knows. The point in a track where the mix starts decides what should follow it. If this is null,
    /// the track's average is used, and an average cannot tell a breakdown from a peak.</param>
    public static MixTrack? Next(
        MixTrack? playing,
        double playingBpm,
        IEnumerable<MixTrack> candidates,
        double? playingIntensity = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        // With nothing playing there is nothing to match, so take the first record offered.
        if (playing is null)
            return candidates.FirstOrDefault();

        var key = playing.Key;
        double intensity = playingIntensity ?? playing.Intensity;
        double bpm = playingBpm > 0 ? playingBpm : playing.Bpm;

        // The read records, if there are any. Otherwise every record is used. That happens when the
        // analyses have not started to arrive yet.
        var offered = candidates as IReadOnlyList<MixTrack> ?? [.. candidates];
        var read = offered.Where(c => c.IsAnalyzed).ToList();
        if (read.Count > 0)
            offered = read;

        MixTrack? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (var candidate in offered)
        {
            double score = Score(candidate, bpm, key, intensity);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>
    /// Lays out a whole set by running <see cref="Next"/> from the opening record. Each choice is made
    /// against what would be playing at that point, which is how a DJ builds a set. The opener is the
    /// crate's first record, so the first track of a playlist still opens the set.
    /// </summary>
    public static IReadOnlyList<MixTrack> Order(IReadOnlyList<MixTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count <= 2)
            return tracks;

        var remaining = new List<MixTrack>(tracks);
        var opener = remaining[0];
        remaining.RemoveAt(0);

        return [opener, .. Following(opener, opener.Bpm, remaining)];
    }

    /// <summary>
    /// The order the rest of a set would come out in, starting from the record playing now. Each choice
    /// is made against what would be on the deck at that point. This matches what the crate does at
    /// each changeover.
    ///
    /// <para>This is a projection, not a promise. The queue shows it during a set. It is worked out
    /// again each time a record is handed over or a reading arrives. A record that nobody has read yet
    /// cannot be placed properly. It takes its place as soon as it can be read.</para>
    /// </summary>
    public static IReadOnlyList<MixTrack> Following(
        MixTrack? playing, double playingBpm, IReadOnlyList<MixTrack> remaining)
    {
        ArgumentNullException.ThrowIfNull(remaining);
        if (remaining.Count <= 1)
            return remaining;

        var left = new List<MixTrack>(remaining);
        var ordered = new List<MixTrack>(remaining.Count);

        var current = playing;
        double bpm = playingBpm;

        while (left.Count > 0)
        {
            var next = (current is null ? left[0] : Next(current, bpm, left)) ?? left[0];
            left.Remove(next);
            ordered.Add(next);
            current = next;
            bpm = next.Bpm;
        }

        return ordered;
    }
}
