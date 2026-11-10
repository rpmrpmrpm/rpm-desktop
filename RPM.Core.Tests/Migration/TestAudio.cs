using System.Security.Cryptography;

namespace RPM.Core.Tests;

/// <summary>Small audio files for import tests.</summary>
internal static class TestAudio
{
    /// <summary>Writes a WAV file of <paramref name="seconds"/> seconds. Different seeds give different
    /// samples, so different audio.</summary>
    public static byte[] Wav(int seconds = 1, int seed = 0)
    {
        const int rate = 8000;
        var samples = rate * seconds;
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(samples * 2);
            for (var i = 0; i < samples; i++)
                w.Write((short)((i * (seed + 1) * 37) % 2000));
        }
        return stream.ToArray();
    }

    /// <summary>A stand-in for the canonical audio hash: the SHA-256 of the WAV's sample data, which
    /// stays the same when tags are written into the file.</summary>
    public static Task<string> HashSamples(string path, CancellationToken ct)
    {
        var bytes = File.ReadAllBytes(path);
        var at = bytes.AsSpan().IndexOf("data"u8);
        var data = at < 0 ? bytes : bytes[(at + 8)..Math.Min(bytes.Length, at + 8 + BitConverter.ToInt32(bytes, at + 4))];
        return Task.FromResult(Convert.ToHexStringLower(SHA256.HashData(data)));
    }
}
