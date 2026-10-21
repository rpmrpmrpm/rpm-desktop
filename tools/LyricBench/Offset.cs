using RPM.Core.Audio;
using RPM.Core.Lyrics;
using RPM.Onnx;

namespace LyricBench;

/// <summary>
/// <c>--offset</c>: runs <see cref="SheetOffset"/> on real records, as the transcoder does, and prints what it found.
///
/// <para>A real sheet that is out of time is rare and has no known answer. So <c>--move</c> makes one: it moves every
/// stamp of a sheet that is in time by a known amount. The search should then find minus that amount, and zero on the
/// sheet as it is. Several amounts can be given at once, separated by commas, so the record is heard only once.</para>
/// </summary>
internal static class Offset
{
    public static int Run(Placement placement, List<(string Audio, string Lrc)> pairs, string? moves, string? cache)
    {
        if (placement.Vocabulary is not { } alphabet)
        {
            Console.Error.WriteLine("no alphabet beside the weights");
            return 1;
        }

        var amounts = (moves ?? "0").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(double.Parse).ToList();
        var asked = 0;
        var shifted = 0;
        var astray = 0;

        foreach (var (audio, lrc) in pairs)
        {
            var sheet = LyricSheet.Parse(File.ReadAllText(lrc));
            if (!sheet.IsSynced)
            {
                Console.WriteLine($"{Path.GetFileName(audio)}: the sheet has no stamps");
                continue;
            }

            var started = DateTime.UtcNow;
            var heard = Cached(audio, cache) ?? Heard(placement, alphabet, audio, cache);
            if (heard is null)
            {
                Console.WriteLine($"{Path.GetFileName(audio)}: the model did not read it");
                continue;
            }

            var heardIn = (DateTime.UtcNow - started).TotalSeconds;
            Console.WriteLine($"{Path.GetFileName(audio)}: {heard.Frames * heard.FrameSeconds:F0}s heard in {heardIn:F1}s");

            // Where each shifted answer puts the sheet as it was before it was moved. They should all be the same place.
            var sits = new List<double>();
            foreach (var move in amounts)
            {
                var moved = sheet.Lines.Select(l => l with { Seconds = l.Seconds + move }).ToList();
                var searched = DateTime.UtcNow;
                var found = SheetOffset.Find(heard, moved, alphabet);
                asked++;
                if (found.Seconds != 0)
                {
                    shifted++;
                    sits.Add(found.Seconds + move);
                }
                Console.WriteLine(
                    $"  moved {move,6:+0.00;-0.00;0}s  sits {found.Best + move,6:+0.00;-0.00;0}s  found {found.Seconds,6:+0.00;-0.00;0}s  best {found.Best,6:+0.00;-0.00;0}s  " +
                    $"{found.Agree,3} of {found.Lines,3} lines agree  {(DateTime.UtcNow - searched).TotalSeconds:F1}s  " +
                    $"{found.Why}");
            }

            if (sits.Count > 0)
            {
                var middle = sits.Order().ElementAt(sits.Count / 2);
                var away = sits.Count(x => Math.Abs(x - middle) > 0.1);
                astray += away;
                Console.WriteLine($"  {sits.Count} shifted, the sheet as it was at {middle:+0.00;-0.00;0}s, {away} elsewhere");
            }
        }

        Console.WriteLine();
        // There is no ground truth for where the sheet as it was belongs. What can be checked is that every move gives
        // the same answer: a shift that puts it somewhere else than the others is wrong.
        Console.WriteLine($"{shifted} of {asked} shifted; {astray} of them put the sheet somewhere else than the other moves did");
        return 0;
    }

    /// <summary>Hears the whole record, and keeps the answer in [cache] when one is given.</summary>
    private static HeardRecord? Heard(Placement placement, CtcVocabulary alphabet, string audio, string? cache)
    {
        var mono = PcmDecoder.DecodeMono(audio, OnnxAligner.SampleRate);
        var heard = SheetOffset.Hear(mono, OnnxAligner.SampleRate, OnnxAligner.SamplesPerFrame, alphabet.Blank,
            window => placement.Read(window) is { } h ? new HeardWindow(h.Scores, h.Frames, h.Vocab) : null);
        if (heard is null || cache is null)
            return heard;

        Directory.CreateDirectory(cache);
        using var writer = new BinaryWriter(File.Create(CachePath(audio, cache)));
        writer.Write(heard.Frames);
        writer.Write(heard.Vocab);
        writer.Write(heard.Blank);
        writer.Write(heard.FrameSeconds);
        for (var f = 0; f < heard.Frames; f++)
            for (var v = 0; v < heard.Vocab; v++)
                writer.Write((float)heard.At(f, v));
        return heard;
    }

    /// <summary>A record heard before with the same <c>--cache</c>, or null.</summary>
    private static HeardRecord? Cached(string audio, string? cache)
    {
        if (cache is null || !File.Exists(CachePath(audio, cache)))
            return null;
        using var reader = new BinaryReader(File.OpenRead(CachePath(audio, cache)));
        var frames = reader.ReadInt32();
        var vocab = reader.ReadInt32();
        var blank = reader.ReadInt32();
        var seconds = reader.ReadDouble();
        var log = new float[frames * vocab];
        for (var i = 0; i < log.Length; i++)
            log[i] = reader.ReadSingle();
        return new HeardRecord(log, frames, vocab, blank, seconds);
    }

    private static string CachePath(string audio, string cache) =>
        Path.Combine(cache, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(audio))))[..16] + ".heard");
}
