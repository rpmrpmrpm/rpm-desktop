using System.IO.Compression;
using System.Numerics;

namespace RPM.Fingerprint.Chroma;

/// <summary>
/// A chroma fingerprint is just a sequence of uint32 sub-fingerprints (one per chroma frame).
/// This type owns serialization (delta compression) and the comparison primitives.
/// </summary>
public readonly struct ChromaFingerprint
{
    public readonly uint[] SubFingerprints;

    public ChromaFingerprint(uint[] subFingerprints)
    {
        SubFingerprints = subFingerprints;
    }

    public int Length => SubFingerprints.Length;

    /// <summary>
    /// Hamming distance between corresponding sub-fingerprints in two aligned sequences.
    /// Returns the SUM of bit differences over the overlapping region. Lower is better.
    /// </summary>
    public static int HammingDistance(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b, int offset)
    {
        // offset is the position of `a[0]` within `b`. A negative offset means a starts before b.
        // We compare only the overlapping region.
        int aStart = Math.Max(0, -offset);
        int bStart = Math.Max(0, offset);
        int len = Math.Min(a.Length - aStart, b.Length - bStart);
        if (len <= 0) return int.MaxValue;

        int total = 0;
        for (int i = 0; i < len; i++)
            total += BitOperations.PopCount(a[aStart + i] ^ b[bStart + i]);
        return total;
    }

    /// <summary>
    /// Best (offset, distance) pair when sliding `query` over `db`. Distance is normalized to bits per frame
    /// so that short and long overlaps are comparable.
    /// </summary>
    public static (int Offset, double BitsPerFrame, int OverlapFrames) BestAlignment(
        uint[] query, uint[] db, int searchStart, int searchEnd)
    {
        int bestOffset = 0;
        double bestBpf = double.MaxValue;
        int bestOverlap = 0;

        for (int off = searchStart; off <= searchEnd; off++)
        {
            int aStart = Math.Max(0, -off);
            int bStart = Math.Max(0, off);
            int len = Math.Min(query.Length - aStart, db.Length - bStart);
            if (len < 8) continue; // need at least a tiny overlap to mean anything

            int total = 0;
            for (int i = 0; i < len; i++)
                total += BitOperations.PopCount(query[aStart + i] ^ db[bStart + i]);

            double bpf = (double)total / len;
            if (bpf < bestBpf)
            {
                bestBpf = bpf;
                bestOffset = off;
                bestOverlap = len;
            }
        }
        return (bestOffset, bestBpf, bestOverlap);
    }

    // ─── Serialization ──────────────────────────────────────────────────────────────────
    //
    // Delta-compress consecutive sub-fingerprints by XOR (adjacent frames usually differ in
    // only a few bits, so the XORs are mostly small numbers). Then variable-length encode
    // each XOR using a simple LEB128 scheme. Stores the first sub-fingerprint as a literal.

    public byte[] Serialize()
    {
        if (SubFingerprints.Length == 0) return Array.Empty<byte>();

        // Stage 1: delta-XOR + LEB128 varint encoding (most XORs are small numbers).
        using var inner = new MemoryStream();
        WriteUInt32LE(inner, (uint)SubFingerprints.Length);
        WriteUInt32LE(inner, SubFingerprints[0]);

        uint prev = SubFingerprints[0];
        for (int i = 1; i < SubFingerprints.Length; i++)
        {
            uint cur = SubFingerprints[i];
            uint delta = cur ^ prev;
            WriteVarint(inner, delta);
            prev = cur;
        }

        // Stage 2: Deflate-compress the varint stream. The varint output still has structure
        // (repeated bit patterns, similar delta sizes) that Deflate's LZ77 + Huffman exploits well.
        // Brotli would compress slightly better but Deflate is in the BCL with no extra deps.
        using var outer = new MemoryStream();
        using (var deflate = new DeflateStream(outer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            inner.Position = 0;
            inner.CopyTo(deflate);
        }
        return outer.ToArray();
    }

    public static ChromaFingerprint Deserialize(byte[] blob)
    {
        if (blob.Length == 0) return new ChromaFingerprint(Array.Empty<uint>());

        // Stage 2 reverse: Deflate-decompress to recover the varint stream
        using var outer = new MemoryStream(blob);
        using var deflate = new DeflateStream(outer, CompressionMode.Decompress);
        using var inner = new MemoryStream();
        deflate.CopyTo(inner);
        inner.Position = 0;

        // Stage 1 reverse: read length + first literal + delta-XORs
        uint count = ReadUInt32LE(inner);
        if (count == 0) return new ChromaFingerprint(Array.Empty<uint>());

        var subs = new uint[count];
        subs[0] = ReadUInt32LE(inner);
        uint prev = subs[0];
        for (int i = 1; i < count; i++)
        {
            uint delta = ReadVarint(inner);
            uint cur = prev ^ delta;
            subs[i] = cur;
            prev = cur;
        }
        return new ChromaFingerprint(subs);
    }

    private static void WriteUInt32LE(Stream s, uint v)
    {
        s.WriteByte((byte)(v));
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 24));
    }

    private static uint ReadUInt32LE(Stream s)
    {
        int a = s.ReadByte(), b = s.ReadByte(), c = s.ReadByte(), d = s.ReadByte();
        if ((a | b | c | d) < 0) throw new EndOfStreamException();
        return (uint)(a | (b << 8) | (c << 16) | (d << 24));
    }

    private static void WriteVarint(Stream s, uint v)
    {
        while (v >= 0x80)
        {
            s.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        s.WriteByte((byte)v);
    }

    private static uint ReadVarint(Stream s)
    {
        uint result = 0;
        int shift = 0;
        while (true)
        {
            int b = s.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 28) throw new InvalidDataException("varint too large");
        }
    }
}
