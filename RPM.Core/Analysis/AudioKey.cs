using System.Security.Cryptography;
using System.Text;

namespace RPM.Core.Analysis;

/// <summary>
/// The name an analysis is stored under: a hash of what the track <em>sounds like</em>, not of the
/// file that holds it.
///
/// <para>Everything the analyser works out (tempo, grid, key, energy, cue points) is a property of
/// the audio. The row that stores it is stamped with a path, a size and a modification time, which
/// belong to one file on one disk. So a copied library would be analysed again, and writing a genre
/// into a file would throw its beat grid away. This key does not have that problem.</para>
///
/// <para>Two files that decode to the same samples get the same key, whatever their containers,
/// tags or compression settings. A FLAC and an ALAC of the same CD track share one key. A
/// re-encode does not, and must not: its grid has really moved.</para>
///
/// <para>A decoder is not required to produce identical samples for a lossy stream, and no two
/// resamplers agree to the last bit. So <b>the decode chain is hashed together with the audio</b>:
/// ffmpeg's reading of a track and AVFoundation's reading of the same track are two different keys.
/// A key that does not match is only a miss, and a miss costs one analysis that would have been done
/// anyway. A key that matched wrongly would cost a wrong downbeat.</para>
/// </summary>
public static class AudioKey
{
    /// <summary>
    /// Increase this whenever anything that goes into the hash changes (the quantisation, the
    /// header, the layout). Old keys then stop being found, instead of being found and trusted.
    /// </summary>
    public const int KeyVersion = 1;

    /// <summary>The desktop's decode chain: ffmpeg, through <see cref="Audio.PcmDecoder"/>.</summary>
    public const string Ffmpeg = "ffmpeg";

    /// <summary>The phone's decode chain: AVAudioFile, which is not ffmpeg.</summary>
    public const string AvFoundation = "avf";

    /// <summary>
    /// The key for a decoded track.
    /// </summary>
    /// <param name="mono">The samples the analyser reads, in -1..1.</param>
    /// <param name="sampleRate">The sample rate of <paramref name="mono"/>. It is part of the key,
    /// because the same track read at two rates is two different sets of samples.</param>
    /// <param name="decoder">Which chain produced the samples: <see cref="Ffmpeg"/> or
    /// <see cref="AvFoundation"/>. It is part of the key on purpose (see the type's remarks).</param>
    /// <returns>Lower-case hex SHA-256, or null when there is nothing to hash.</returns>
    public static string? OfSamples(ReadOnlySpan<float> mono, int sampleRate, string decoder)
    {
        if (mono.Length == 0 || sampleRate <= 0 || string.IsNullOrEmpty(decoder))
            return null;

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Everything that decides what the samples are is written in front of them. Two runs that
        // differ in any of it get different keys by construction.
        sha.AppendData(Encoding.UTF8.GetBytes($"rpm-audio-key/{KeyVersion}\n{decoder}\n{sampleRate}\n{mono.Length}\n"));

        // Quantised to 16 bits, little-endian: the sample format the audio came from, not the float
        // format it is carried in. Floats would put the endianness, the sign of zero and the scaling
        // path into the key, and none of those say anything about the track. This is not noise
        // tolerance (a hash has none). It is a canonical form.
        var block = new byte[ChunkSamples * 2];
        for (int at = 0; at < mono.Length; at += ChunkSamples)
        {
            int count = Math.Min(ChunkSamples, mono.Length - at);
            for (int i = 0; i < count; i++)
            {
                short q = Quantize(mono[at + i]);
                block[i * 2] = (byte)q;
                block[i * 2 + 1] = (byte)(q >> 8);
            }
            sha.AppendData(block, 0, count * 2);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>How many samples are converted to bytes at a time. Converting a whole track at once
    /// would make a second copy of a buffer that is already tens of megabytes.</summary>
    private const int ChunkSamples = 16384;

    /// <summary>
    /// One sample as a 16-bit integer. It is rounded half away from zero and clamped. The rounding
    /// makes a sample exactly on a boundary land the same way on every machine. The clamp stops a
    /// decoder that overshoots -1..1 (which decoders legitimately do) from wrapping the value round.
    /// </summary>
    private static short Quantize(float sample)
    {
        if (float.IsNaN(sample))
            return 0;
        double scaled = Math.Round(sample * 32767.0, MidpointRounding.AwayFromZero);
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }
}
