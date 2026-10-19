using System.Buffers.Binary;
using System.IO.Compression;

namespace RPM.Core.Cloud;

/// <summary>How each value of a window, or of the answer to one, is written on the wire.</summary>
public enum GridPrecision
{
    /// <summary>Sixteen bits. Older apps send this precision and read the answer in it, so the function
    /// still answers in it.</summary>
    Half,

    /// <summary>Thirty-two bits, the model's own. The current app sends this.</summary>
    Single,
}

/// <summary>
/// How a window of a record travels to the store's beat model and how its answer comes back.
///
/// <para><b>Why a window and not the whole record.</b> A machine that cannot run the beat model can
/// still decode a file and take its spectrogram. That part is an FFT and costs almost nothing. What
/// it cannot carry is the transformer. So the split is made exactly there: the mel spectrogram is
/// built here, one thirty second window at a time, and only the window crosses the wire. A four
/// minute record is nine windows, at most about 9 MB in total at single precision, against thirty
/// megabytes for the file itself, and nothing is uploaded that the store would have to keep.</para>
///
/// <para><b>Why single precision.</b> The grid this produces is published to the shared pool, where
/// two reports of a record agree only when they are the same text. At half precision, on one test
/// window, the beat output moved by up to 0.0023 per frame and its sum by 0.17. The same window on
/// two processors, or on DirectML, differed by 0.002 in the sum. So half precision would make a slow
/// machine's grid differ from a fast machine's for a reason the pool cannot see. At single precision
/// the window reaches the model exactly as the app would have fed it.
/// <see cref="GridPrecision.Half"/> stays for the older apps that send it.</para>
///
/// <para>The app and the function share this file so that the two cannot disagree about what a
/// window is: this file is the format.</para>
/// </summary>
public static class GridWire
{
    /// <summary>The precision's name in a request (<c>precision</c>).</summary>
    public static string Name(GridPrecision precision) => precision switch
    {
        GridPrecision.Half => "half",
        GridPrecision.Single => "single",
        _ => throw new ArgumentOutOfRangeException(nameof(precision), precision, null),
    };

    /// <summary>The precision a request names, or null for a name that is not one. A request that
    /// names none was sent by an older app, which packs at half precision.</summary>
    public static GridPrecision? Named(string? name) => name switch
    {
        null => GridPrecision.Half,
        "half" => GridPrecision.Half,
        "single" => GridPrecision.Single,
        _ => null,
    };

    /// <summary>Packs floats for the wire at <paramref name="precision"/>, deflated, then base64. The
    /// order is the tensor's own, row after row.</summary>
    public static string Pack(ReadOnlySpan<float> values, GridPrecision precision)
    {
        int width = Width(precision);
        var bytes = new byte[values.Length * width];
        for (int i = 0; i < values.Length; i++)
        {
            if (precision is GridPrecision.Half)
                BinaryPrimitives.WriteInt16LittleEndian(
                    bytes.AsSpan(i * width, width), BitConverter.HalfToInt16Bits((Half)values[i]));
            else
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * width, width), values[i]);
        }

        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(bytes, 0, bytes.Length);

        return Convert.ToBase64String(packed.ToArray());
    }

    /// <summary>Unpacks what <see cref="Pack"/> made at the same precision. Returns null when the input
    /// is not that: a truncated body, a count that does not match what was asked for, or values written
    /// at the other precision. The caller's answer to a window that it cannot read is to stop and let
    /// the record be blended, not to guess at beats.</summary>
    public static float[]? Unpack(string? packed, int count, GridPrecision precision)
    {
        if (string.IsNullOrEmpty(packed) || count <= 0)
            return null;

        try
        {
            using var source = new MemoryStream(Convert.FromBase64String(packed));
            using var deflate = new DeflateStream(source, CompressionMode.Decompress);
            using var flat = new MemoryStream();
            deflate.CopyTo(flat);

            int width = Width(precision);
            var bytes = flat.ToArray();
            if (bytes.Length != count * width)
                return null;

            var values = new float[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = precision is GridPrecision.Half
                    ? (float)BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * width, width)))
                    : BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * width, width));
            }

            return values;
        }
        catch
        {
            return null;
        }
    }

    private static int Width(GridPrecision precision) => precision switch
    {
        GridPrecision.Half => 2,
        GridPrecision.Single => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(precision), precision, null),
    };
}
