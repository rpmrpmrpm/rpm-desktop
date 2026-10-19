using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RPM.Core.Audio;
using RPM.Core.Lyrics;
using RPM.Onnx;

namespace LyricCorpus;

/// <summary>
/// Builds a fine-tuning corpus from a collection that already has synced sheets: one clip per lyric
/// line, at the aligner's own sample rate, with the words that are sung in it.
///
/// <para><b>Why a record collection and not a research corpus.</b> DALI is the usual choice, but it
/// has three problems: its annotations need access approval, its audio is not distributed (you
/// download it yourself by YouTube id), and its word times were produced by teacher-student
/// alignment, so they are not ground truth either. A collection has none of these problems: the
/// audio is already here and already licensed, and it is the same kind of audio the app plays.</para>
///
/// <para><b>What a training pair is.</b> CTC needs audio and the text sung in it. It does not need
/// word times and finds the alignment itself, so it can be trained from an LRC that gives only where
/// each line STARTS. One line, its range of the record, its words.</para>
///
/// <para><b>The range comes from <see cref="LyricWindow"/>.</b> The model sees exactly that window at
/// inference, so training it on anything else (for example the whole gap to the next line, with
/// thirty seconds of guitar after a four-word hook) teaches it about audio it never sees, and teaches
/// it that most audio has no words. One rule in one place, used for both training and
/// inference.</para>
///
/// <para>Nothing here writes into the collection. It reads records and sheets, and writes clips and a
/// manifest into its own folder.</para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (Arg(args, "--manifest") is not { } manifest || !File.Exists(manifest))
        {
            Console.Error.WriteLine("give --manifest: lines of \"<record>TAB<sheet>\"");
            return 1;
        }

        if (Arg(args, "--out") is not { } outRoot)
        {
            Console.Error.WriteLine("give --out: a folder to write the clips and the manifest into");
            return 1;
        }

        var models = Arg(args, "--models")
                     ?? Path.Combine("..", "..", "RPM.Desktop", "Assets", "OnnxModels");

        // The model's own alphabet, so a line it cannot spell is dropped here instead of becoming a
        // training pair with half its transcript missing. The common case is a sheet in a language
        // these weights do not know, which affects a whole record.
        var alphabet = new OnnxAligner(models).Vocabulary;
        if (alphabet is null)
        {
            Console.Error.WriteLine($"no vocabulary under {models} — run scripts/fetch-models.sh");
            return 1;
        }

        var hours = Arg(args, "--hours") is { } h && double.TryParse(h, out var asked) ? asked : double.MaxValue;

        var clips = Path.Combine(outRoot, "clips");
        Directory.CreateDirectory(clips);

        using var index = new StreamWriter(Path.Combine(outRoot, "corpus.tsv"), append: false, Encoding.UTF8);
        index.WriteLine("clip\tseconds\ttext");

        var tally = new Tally();

        foreach (var line in File.ReadLines(manifest))
        {
            if (tally.Seconds / 3600.0 >= hours)
                break;

            var parts = line.Split('\t');
            if (parts.Length < 2 || !File.Exists(parts[0]) || !File.Exists(parts[1]))
                continue;

            Cut(parts[0], parts[1], alphabet, clips, index, tally);
            Console.Write($"\r{tally.Records} records, {tally.Clips} clips, {tally.Seconds / 3600.0:F1}h");
        }

        Console.WriteLine();
        Console.WriteLine(tally.Report());
        return 0;
    }

    private const string Usage =
        """
        lyriccorpus --manifest <pairs> --out <folder> [options]

          --manifest  Lines of "<record>TAB<sheet>". The sheet must be synced.
          --out       Where the clips and corpus.tsv are written. Nothing else is touched.
          --models    Folder holding the weights and their vocab, for the alphabet a line must be
                      spellable in (default: RPM.Desktop's assets).
          --hours     Stop once this many hours have been cut. A pilot does not need the whole
                      collection, and the disk here is not large.

        Every clip is 16 kHz mono 16-bit PCM — the rate the aligner reads at — and covers exactly the
        window the aligner would be given for that line at inference.
        """;

    /// <summary>The shortest clip used for training. Below this there is not enough audio for the
    /// words, and CTC refuses a transcript longer than its frames.</summary>
    private const double LeastSeconds = 1.0;

    /// <summary>The longest clip used for training. A line whose window is this long is followed by an
    /// instrumental part, and would mostly teach the model that singing is rare.</summary>
    private const double MostSeconds = 14.0;

    /// <summary>How much of a line must be in the model's alphabet for the line to be kept. A few
    /// accents are fine; a sheet in another script is a whole record of useless data.</summary>
    private const double MustSpell = 0.9;

    /// <summary>
    /// How much of a sheet must be common English words for the record to be treated as English.
    ///
    /// <para><b>The alphabet check cannot decide this.</b> "Un jour, un bourgeon émerge discrètement"
    /// is 94% A to Z, so a model that knows only English letters can spell nearly all of it, and the
    /// line passes the alphabet check. It would then teach the model wrong sounds for those letters.
    /// Language is a property of the SHEET, not of a character, so it is checked once per
    /// record.</para>
    ///
    /// <para>A tenth leaves a wide margin on both sides: English lyrics have three or four times that
    /// (measured lines are between 25 and 40%), and a French or Dutch sheet is close to zero. The list
    /// below leaves out words that exist in several languages ("a", "on", "no", "me", "son"), so every
    /// word it counts is real evidence of English.</para>
    /// </summary>
    private const double MustBeEnglish = 0.10;

    private static readonly HashSet<string> Everyday = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "you", "your", "that", "this", "with", "have", "what", "when", "will", "they",
        "are", "was", "were", "know", "not", "but", "all", "just", "like", "get", "got", "cant",
        "dont", "im", "its", "never", "would", "could", "should", "about", "there", "been", "from",
        "into", "down", "right", "feel", "make", "take", "come", "going", "gonna", "wanna", "only",
        "every", "way", "back", "time", "away", "again", "always", "she", "he", "we", "why", "who",
    };

    /// <summary>Whether a sheet is in the language these weights were trained on. Checked for the
    /// whole record at once: a sheet does not change language in the middle.</summary>
    private static bool English(LyricSheet sheet)
    {
        int words = 0, everyday = 0;
        foreach (var line in sheet.Lines)
            foreach (var word in (line.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var bare = new string([.. word.Where(char.IsLetter)]);
                if (bare.Length == 0)
                    continue;
                words++;
                if (Everyday.Contains(bare))
                    everyday++;
            }

        return words > 0 && everyday >= words * MustBeEnglish;
    }

    private static void Cut(
        string audio, string lrc, CtcVocabulary alphabet, string clips, StreamWriter index, Tally tally)
    {
        LyricSheet sheet;
        try
        {
            sheet = LyricSheet.Parse(File.ReadAllText(lrc, Encoding.UTF8));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\n{Path.GetFileName(lrc)}: unreadable ({ex.GetType().Name})");
            return;
        }

        if (!sheet.IsSynced)
            return;

        // Checked before the record is decoded, which is the expensive part: a sheet in another
        // language gives no training pairs, so there is no reason to decode the audio.
        if (!English(sheet))
        {
            tally.NotEnglish++;
            return;
        }

        float[] mono;
        try
        {
            mono = PcmDecoder.DecodeMono(audio, OnnxAligner.SampleRate);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\n{Path.GetFileName(audio)}: would not decode ({ex.GetType().Name})");
            return;
        }

        if (mono.Length == 0)
            return;

        tally.Records++;

        for (var i = 0; i < sheet.Lines.Count; i++)
        {
            var text = (sheet.Lines[i].Text ?? "").Trim();
            if (text.Length == 0)
                continue;

            if (!Spellable(text, alphabet))
            {
                tally.Unspellable++;
                continue;
            }

            var from = sheet.Lines[i].Seconds;
            var until = i + 1 < sheet.Lines.Count ? sheet.Lines[i + 1].Seconds : double.PositiveInfinity;

            // The same range that the aligner gets for this line. See the note on the class.
            var window = LyricWindow.For(text, from, until);
            if (window.Length < LeastSeconds || window.Length > MostSeconds)
            {
                tally.Skipped++;
                continue;
            }

            var at = (int)(window.Opens * OnnxAligner.SampleRate);
            if (at >= mono.Length)
                continue;

            var take = Math.Min((int)(window.Length * OnnxAligner.SampleRate), mono.Length - at);
            if (take < LeastSeconds * OnnxAligner.SampleRate)
                continue;

            // Named by the record and the line, not by a counter, so building the corpus twice gives
            // the same corpus, and a second run overwrites files instead of adding copies.
            var name = Name(audio, i);
            Write(Path.Combine(clips, name), mono.AsSpan(at, take));

            index.WriteLine($"clips/{name}\t{take / (double)OnnxAligner.SampleRate:F2}\t{text}");
            tally.Clips++;
            tally.Seconds += take / (double)OnnxAligner.SampleRate;
        }
    }

    /// <summary>Whether the model's alphabet covers enough of this line for it to be useful.</summary>
    private static bool Spellable(string text, CtcVocabulary alphabet)
    {
        int letters = 0, known = 0;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c))
                continue;
            letters++;
            if (alphabet.Knows(c))
                known++;
        }

        return letters > 0 && known >= letters * MustSpell;
    }

    private static string Name(string audio, int line)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(audio)))[..12];
        return $"{hash}-{line:D3}.wav";
    }

    /// <summary>One clip, in the simplest format that every tool reads: 16 kHz mono 16-bit PCM.</summary>
    private static void Write(string path, ReadOnlySpan<float> samples)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(file);

        var bytes = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(OnnxAligner.SampleRate);
        w.Write(OnnxAligner.SampleRate * 2);
        w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);

        foreach (var sample in samples)
            w.Write((short)Math.Clamp(sample * short.MaxValue, short.MinValue, short.MaxValue));
    }

    private sealed class Tally
    {
        public int Records, Clips, Skipped, Unspellable, NotEnglish;
        public double Seconds;

        public string Report() =>
            $"""
             {Records} records, {Clips} clips, {Seconds / 3600.0:F1} hours
                mean clip                     {Seconds / Math.Max(1, Clips):F1}s
                lines skipped for length      {Skipped}
                lines the model cannot spell  {Unspellable}
                records not in English        {NotEnglish}
             """;
    }

    private static string? Arg(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
