using RPM.Core.Audio;

namespace RPM.Core.Mixing;

/// <summary>
/// The booth: two decks and the mixer they run into. It reads a block of audio from each deck and adds
/// the two blocks together. It does not decide what is playing or when the fader moves. The
/// <see cref="Dj"/> sets the levels. The booth only sums the two decks.
/// </summary>
public sealed class DjBooth : IDisposable
{
    private readonly int _channels;
    private float[] _deckBuffer = [];

    public DjBooth(int sampleRate, int channels, Func<FfmpegPcmSource.Options, IPcmSource>? openSource = null)
    {
        _channels = channels;
        Left = new Deck(Deck.Side.A, sampleRate, channels, openSource);
        Right = new Deck(Deck.Side.B, sampleRate, channels, openSource);
    }

    /// <summary>The two decks. Which one is live changes with every transition. Ask the
    /// <see cref="Dj"/> which deck is live, not the booth.</summary>
    public Deck Left { get; }
    public Deck Right { get; }

    /// <summary>The deck that is not <paramref name="deck"/>. The next record goes there.</summary>
    public Deck Other(Deck deck) => ReferenceEquals(deck, Left) ? Right : Left;

    /// <summary>
    /// Mixes both decks into <paramref name="output"/>. Each deck applies its own fader and bass shelf
    /// while it reads, so this is a plain sum. The result is clamped, because two records playing at
    /// once can go past full scale. A sample that wraps around is heard as a click.
    /// </summary>
    /// <returns>Frames written. Zero only when both decks are silent and finished, which is the end of
    /// the set.</returns>
    public int Mix(float[] output, int frames)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (frames <= 0)
            return 0;

        int samples = frames * _channels;
        Array.Clear(output, 0, samples);
        if (_deckBuffer.Length < samples)
            _deckBuffer = new float[samples];

        int written = 0;
        written = Math.Max(written, Add(Left, output, frames));
        written = Math.Max(written, Add(Right, output, frames));
        if (written == 0)
            return 0;

        for (int i = 0; i < samples; i++)
            output[i] = Math.Clamp(output[i], -1f, 1f);

        return written;
    }

    private int Add(Deck deck, float[] output, int frames)
    {
        if (!deck.IsLoaded)
            return 0;

        int read = deck.Read(_deckBuffer, frames);
        if (read <= 0)
            return 0;

        int samples = read * _channels;
        for (int i = 0; i < samples; i++)
            output[i] += _deckBuffer[i];
        return read;
    }

    public void Dispose()
    {
        Left.Dispose();
        Right.Dispose();
    }
}
