using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RPM.Core.Splitting;
using RPM.Fingerprint.Catalog;

namespace RPM.Core.Metadata.Providers;

/// <summary>What the linker makes of one album.</summary>
public enum LinkVerdict
{
    /// <summary>One record fits beyond doubt: the names agree, the track count is the same and the lengths line
    /// up. Safe to link without asking.</summary>
    Link,

    /// <summary>Something with the right names came back, but the tracks do not prove it: another edition, a
    /// listing with no times, lengths that do not line up, or two different records that both fit. Left for the
    /// listener.</summary>
    Unsure,

    /// <summary>Nothing with this artist and title.</summary>
    NoMatch,
}

/// <summary>A release the search found, with the listing fetched for it.</summary>
public sealed record LinkCandidate(ReleaseSearchResult Release, ReleaseDetail Detail);

/// <summary>The linker's answer for one album.</summary>
/// <param name="Best">The release that fits best, or null when nothing had the right names.</param>
/// <param name="Match">How its tracks compare with the album's, or null with no best.</param>
/// <param name="Reason">Why, in a few words, for the report.</param>
/// <param name="Partial">Linked on part of the release: every track here is on it, track by track, but it lists
/// more (a single of which one side is here, an album split over folders). See
/// <see cref="ReleaseLinker.PairEvery"/>.</param>
public sealed record LinkDecision(
    LinkVerdict Verdict, LinkCandidate? Best, ReleaseMatch? Match, string Reason, bool Partial = false);

/// <summary>
/// Decides whether an album in the library is a release a catalogue found, from the names and the track lengths.
///
/// <para><b>Only proof links.</b> A link is used for more than styles: the album page shows the release's
/// catalogue number, barcode and year. So a link is made only when the names agree, the track count is the same
/// and the lengths line up (<see cref="LinkScore"/>), and everything short of that is left to the listener. Two
/// pressings of one record have the same listing and tie; they share their styles, so either will do, and a
/// digital one is taken first, because the files most likely came from one and its lengths are the files'
/// own.</para>
///
/// <para>Pure: the search results and their listings are handed in, so a test can state them.</para>
/// </summary>
public static class ReleaseLinker
{
    /// <summary>The length match a link needs: half the score is the right count, the other half closeness, so
    /// 0.8 is a mean error of under two and a half seconds a track. Stricter than
    /// <see cref="ReleaseMatcher.GoodMatch"/>, which fills in a dialog the user still reads.</summary>
    public const double LinkScore = 0.8;

    /// <summary>How far the album may run fast or slow against the listing and still be the same recording. A
    /// file plays at its own speed, so beyond two percent the listing is another master or another record.</summary>
    public const double MostDrift = 0.02;

    /// <summary>How far a track here may be from its track on the release, when the album is linked on part of
    /// it. Each track is matched by its title as well, so the lengths only have to tell one version of a song from
    /// another: a radio edit from the extended mix.</summary>
    public const double PartialToleranceSeconds = 4;

    /// <summary>Whether a release's format is a digital one: a file, a download.</summary>
    public static bool IsDigital(string? format) => ReleaseMedia.Digital.Matches(format);

    /// <summary>
    /// Decides for one album. <paramref name="lengths"/> are its tracks' lengths in play order, discs in turn, and
    /// <paramref name="titles"/> their titles in the same order. With titles, an album that is part of a release
    /// can be linked to it too (see <see cref="PairEvery"/>).
    /// </summary>
    public static LinkDecision Decide(
        string artist, string album, IReadOnlyList<double> lengths, IReadOnlyList<LinkCandidate> candidates,
        IReadOnlyList<string>? titles = null)
    {
        ArgumentNullException.ThrowIfNull(lengths);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
            return new LinkDecision(LinkVerdict.NoMatch, null, null, "nothing found");

        var named = candidates.Where(c => NamesAgree(artist, album, c.Release.Artist ?? c.Detail.Artist, c.Release.Title))
            .ToList();
        if (named.Count == 0)
            return new LinkDecision(LinkVerdict.NoMatch, null, null, "no result with this artist and title");

        var measured = named
            .Select(c => (Candidate: c, Match: ReleaseMatcher.Measure(c.Detail.Tracks.Select(t => t.LengthSeconds).ToList(), lengths)))
            .ToList();

        // Every release left has this album's names, so the ones the tracks prove are pressings of this record.
        var proven = measured.Where(m => Proves(m.Match)).ToList();
        if (proven.Count > 0)
        {
            var best = proven
                .OrderByDescending(m => IsDigital(m.Candidate.Release.Format ?? m.Candidate.Detail.Format))
                .ThenByDescending(m => m.Match.Score)
                .First();
            return new LinkDecision(LinkVerdict.Link, best.Candidate, best.Match, "names, count and lengths agree");
        }

        // Part of a release: the release lists more than is here, and every track here is one of its tracks.
        if (titles is { Count: > 0 } && titles.Count == lengths.Count)
        {
            var part = named
                .Select(c => (Candidate: c, Pairs: PairEvery(titles, lengths, c.Detail.Tracks)))
                .Where(p => p.Pairs is not null)
                .Select(p => (p.Candidate, Match: p.Pairs!))
                .OrderByDescending(p => IsDigital(p.Candidate.Release.Format ?? p.Candidate.Detail.Format))
                .ThenBy(p => p.Match.ExpectedCount)          // the closest edition: the fewest tracks not here
                .ThenBy(p => p.Match.MeanErrorSeconds)
                .FirstOrDefault();
            if (part.Candidate is not null)
                return new LinkDecision(LinkVerdict.Link, part.Candidate, part.Match,
                    $"every track here is on it, {part.Match.DetectedCount} of {part.Match.ExpectedCount}", Partial: true);
        }

        var closest = measured.MaxBy(m => m.Match.Score);
        return new LinkDecision(LinkVerdict.Unsure, closest.Candidate, closest.Match, WhyNot(closest.Match));
    }

    /// <summary>
    /// Pairs every track here with a track on a release that lists more: the same title, edition notes aside, and a
    /// length within <see cref="PartialToleranceSeconds"/>, each listed track used once. Null when any track here
    /// finds no partner, or when the release lists no more than is here (that is a whole-release question, which
    /// <see cref="ReleaseMatcher.Measure"/> answers).
    /// </summary>
    /// <returns>The pairing as a match: the score from how close the lengths are, the listed and the paired counts,
    /// the mean error.</returns>
    public static ReleaseMatch? PairEvery(
        IReadOnlyList<string> titles, IReadOnlyList<double> lengths, IReadOnlyList<ReleaseTrackInfo> listed)
    {
        if (titles.Count == 0 || titles.Count != lengths.Count || listed.Count <= titles.Count)
            return null;

        var used = new bool[listed.Count];
        var folded = listed.Select(t => FoldTitle(t.Title)).ToArray();
        double error = 0;
        for (var i = 0; i < titles.Count; i++)
        {
            var mine = FoldTitle(titles[i]);
            if (mine.Length == 0)
                return null;

            var best = -1;
            var bestGap = double.MaxValue;
            for (var j = 0; j < listed.Count; j++)
            {
                if (used[j] || folded[j] != mine || listed[j].LengthSeconds is not { } length || length <= 0)
                    continue;
                var gap = Math.Abs(length - lengths[i]);
                if (gap <= PartialToleranceSeconds && gap < bestGap)
                {
                    best = j;
                    bestGap = gap;
                }
            }

            if (best < 0)
                return null;
            used[best] = true;
            error += bestGap;
        }

        var mean = error / titles.Count;
        var closeness = Math.Clamp(1 - mean / PartialToleranceSeconds, 0, 1);
        return new ReleaseMatch(0.5 + 0.5 * closeness, MatchBasis.Lengths, listed.Count, titles.Count, mean, 1);
    }

    private static bool Proves(ReleaseMatch match) =>
        match.Basis == MatchBasis.Lengths && match.Score >= LinkScore && Math.Abs(match.DriftFactor - 1) <= MostDrift;

    private static string WhyNot(ReleaseMatch match) => match.Basis switch
    {
        MatchBasis.TotalOnly => $"track count differs: {match.DetectedCount} here, {match.ExpectedCount} listed",
        MatchBasis.CountOnly => "the listing has no track times",
        MatchBasis.None => "the listing has no tracks",
        _ when Math.Abs(match.DriftFactor - 1) > MostDrift => $"runs {Math.Abs(match.DriftFactor - 1):P0} off the listing",
        _ => $"lengths are {match.MeanErrorSeconds:0.#} s a track off",
    };

    // ── names ──

    /// <summary>Whether a release's artist and title are this album's, read loosely: case, accents, punctuation,
    /// edition notes ("(Deluxe Version)", "- Single") and Discogs' own marks ("Madonna (2)", "Sting*") do not
    /// count. An artist agrees when either credit names the other's first act, so "Drake; Travis Scott" is
    /// "Drake & Travis Scott". A compilation agrees with Discogs' "Various".</summary>
    public static bool NamesAgree(string artist, string album, string? theirArtist, string theirTitle) =>
        TitlesAgree(album, theirTitle) && ArtistsAgree(artist, theirArtist);

    public static bool TitlesAgree(string ours, string theirs)
    {
        var a = FoldTitle(ours);
        var b = FoldTitle(theirs);
        return a.Length > 0 && a == b;
    }

    public static bool ArtistsAgree(string ours, string? theirs)
    {
        var a = FoldArtist(ours);
        var b = FoldArtist(theirs);
        if (a.Length == 0 || b.Length == 0)
            return false;
        if (a == b || a.Replace(",", "") == b.Replace(",", ""))
            return true;
        if (IsVarious(a) && IsVarious(b))
            return true;

        // A credit with more than one act agrees when the first act is the same.
        return FirstAct(a) == FirstAct(b);
    }

    /// <summary>The title without its edition notes, in one plain spelling.</summary>
    public static string FoldTitle(string? title)
    {
        var text = title ?? "";
        text = Bracketed.Replace(text, " ");
        text = ReleaseSuffix.Replace(text, "");
        return Plain(text);
    }

    private static string FoldArtist(string? artist)
    {
        var text = artist ?? "";
        text = DiscogsNumber.Replace(text, "");
        text = text.Replace("*", "");
        return Plain(text);
    }

    private static string FirstAct(string folded)
    {
        var cut = ActSeparator.Match(folded);
        return (cut.Success ? folded[..cut.Index] : folded).Trim();
    }

    private static bool IsVarious(string folded) => folded is "various" or "various artists" or "va";

    /// <summary>Lower case, accents off, "&amp;" as "and", a leading "the" dropped, punctuation as spaces, one
    /// space between words.</summary>
    private static string Plain(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;
            if (ch == '&')
                sb.Append(" and ");
            else if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
            else if (ch is ';' or ',' or '/')
                sb.Append(" , ");   // kept as a mark between acts
            else
                sb.Append(' ');
        }

        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0] == "the")
            words.RemoveAt(0);
        return string.Join(' ', words).Replace(" , ", ", ").Trim(' ', ',');
    }

    private static readonly Regex Bracketed = new(@"[\(\[][^\)\]]*[\)\]]", RegexOptions.CultureInvariant);

    private static readonly Regex ReleaseSuffix = new(
        @"\s+[-–—]\s+(?:single|ep|remastered|remaster|deluxe(?: edition| version)?|expanded edition)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DiscogsNumber = new(@"\s*\(\d+\)", RegexOptions.CultureInvariant);

    private static readonly Regex ActSeparator = new(
        @",|\s(?:and|feat|featuring|ft|with|x|vs)\s", RegexOptions.CultureInvariant);
}
