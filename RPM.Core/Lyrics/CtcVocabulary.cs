namespace RPM.Core.Lyrics;

/// <summary>One word of a line, and the frames it was sung over, from a forced alignment.
/// <paramref name="Start"/> and <paramref name="Length"/> index the text that was handed in. A view slices its
/// own displayed string with them.</summary>
public readonly record struct WordFrames(int Start, int Length, int FromFrame, int ToFrame);

/// <summary>
/// A CTC model's alphabet, and the two jobs that need it: turning a line of lyric into the tokens the model
/// was trained on, and turning the aligner's answer back into words.
///
/// <para><b>The alphabet is built from the model's own vocabulary, not written down here.</b> Every CTC model
/// ships the list it was trained with, and the lists differ. They have different orders, different letter
/// cases, and different rules for the apostrophe. A table written into this file would be right for one set of
/// weights and quietly wrong for the next. Only the shape of the problem is fixed here: there is a blank, there
/// is a character that separates words, and everything else is a letter.</para>
///
/// <para>Characters the model has no token for are DROPPED, not replaced with an unknown token. The aligner
/// chooses a path through a known string. A token the model can never emit is a wall across that path. One
/// comma in a lyric would make the whole line impossible to align. Dropping it costs nothing. The letters on
/// either side still land where they were sung, and a comma is not sung.</para>
/// </summary>
public sealed class CtcVocabulary
{
    private readonly Dictionary<char, int> _tokens;

    /// <summary>The blank's id: "nothing new in this frame", the token CTC is built around.</summary>
    public int Blank { get; }

    /// <summary>The id of the character the model uses between words, where it has one.</summary>
    public int? Delimiter { get; }

    /// <param name="tokens">The model's alphabet: which character each token id stands for. Case is folded, so a
    /// model trained on capitals can read a lyric written in either case.</param>
    /// <param name="blank">The blank's id.</param>
    /// <param name="delimiter">The character the model uses to separate words, usually '|'. Null for a model with
    /// none. Its words then run together and are separated only by where they land.</param>
    public CtcVocabulary(IReadOnlyDictionary<char, int> tokens, int blank = 0, char? delimiter = '|')
    {
        ArgumentNullException.ThrowIfNull(tokens);

        _tokens = new Dictionary<char, int>(tokens.Count);
        foreach (var (character, id) in tokens)
            _tokens[char.ToUpperInvariant(character)] = id;

        Blank = blank;
        Delimiter = delimiter is { } d && _tokens.TryGetValue(char.ToUpperInvariant(d), out var did)
            ? did
            : null;
    }

    /// <summary>Whether the model can spell this character at all.</summary>
    public bool Knows(char character) => _tokens.ContainsKey(char.ToUpperInvariant(character));

    /// <summary>
    /// A line of lyric as the model's tokens, and where each token's word sits in the text.
    ///
    /// <para>Returns null when nothing survives. That happens with a line of punctuation, or with a line written
    /// in a script the model has no letters for. The second case is common for a sheet in the wrong
    /// language.</para>
    /// </summary>
    public Spelling? Spell(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var tokens = new List<int>(text.Length);
        var owners = new List<int>(text.Length);   // which word each token belongs to
        var words = new List<(int Start, int Length)>();

        var at = 0;
        while (at < text.Length)
        {
            while (at < text.Length && char.IsWhiteSpace(text[at]))
                at++;

            var start = at;
            while (at < text.Length && !char.IsWhiteSpace(text[at]))
                at++;

            if (at <= start)
                continue;

            // Spelled first, and kept only if something survives. A "word" of pure punctuation has no tokens. A
            // word with no tokens cannot be aligned, so it must not take a slot in the list that the caller reads
            // frame numbers back from.
            var opened = tokens.Count;
            for (var i = start; i < at; i++)
                if (_tokens.TryGetValue(char.ToUpperInvariant(text[i]), out var id))
                {
                    tokens.Add(id);
                    owners.Add(words.Count);
                }

            if (tokens.Count == opened)
                continue;

            words.Add((start, at - start));

            // The separator goes BETWEEN words and belongs to neither, which is what -1 means. It is added before the
            // next word, not after this one, so a trailing separator is never emitted.
            if (Delimiter is { } delimiter && at < text.Length)
            {
                tokens.Add(delimiter);
                owners.Add(-1);
            }
        }

        // A trailing separator can still be left, when every remaining word turned out to be punctuation. It would
        // align to nothing after the last word, and it would stretch that span out.
        while (tokens.Count > 0 && owners[^1] < 0)
        {
            tokens.RemoveAt(tokens.Count - 1);
            owners.RemoveAt(owners.Count - 1);
        }

        return words.Count == 0 ? null : new Spelling([.. tokens], [.. owners], [.. words]);
    }

    /// <summary>
    /// The words of a line, and the frames each was sung over, from the aligner's answer for each token.
    ///
    /// <para>A word runs from the first frame of its first letter to the last frame of its last letter.
    /// Separators between words are ignored. Neither word sings the gap between them.</para>
    /// </summary>
    public static IReadOnlyList<WordFrames> Words(Spelling spelling, IReadOnlyList<TokenFrames> aligned)
    {
        ArgumentNullException.ThrowIfNull(spelling);
        ArgumentNullException.ThrowIfNull(aligned);

        if (aligned.Count != spelling.Tokens.Length)
            return [];   // an answer that is not for this spelling cannot be read against it

        var opens = new int[spelling.Words.Length];
        var closes = new int[spelling.Words.Length];
        Array.Fill(opens, -1);

        for (var i = 0; i < aligned.Count; i++)
        {
            var word = spelling.Owners[i];
            if (word < 0)
                continue;

            if (opens[word] < 0 || aligned[i].FromFrame < opens[word])
                opens[word] = aligned[i].FromFrame;
            if (aligned[i].ToFrame > closes[word])
                closes[word] = aligned[i].ToFrame;
        }

        var found = new List<WordFrames>(spelling.Words.Length);
        for (var w = 0; w < spelling.Words.Length; w++)
            if (opens[w] >= 0)
                found.Add(new WordFrames(
                    spelling.Words[w].Start, spelling.Words[w].Length, opens[w], closes[w]));

        return found;
    }
}

/// <summary>
/// A line of lyric spelled in one model's alphabet: the tokens to align, which word each token belongs to, and
/// where those words sit in the text the line came from.
/// </summary>
/// <param name="Tokens">The transcript, for <see cref="CtcAligner.Align"/>.</param>
/// <param name="Owners">For each token, the index of the word it belongs to, or -1 for a separator.</param>
/// <param name="Words">For each word, its offset and length in the original text.</param>
public sealed record Spelling(int[] Tokens, int[] Owners, (int Start, int Length)[] Words);
