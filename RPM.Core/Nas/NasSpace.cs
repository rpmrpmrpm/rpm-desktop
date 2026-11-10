using RPM.Core.Sharing;

namespace RPM.Core.Nas;

/// <summary>Whether an import fits on the NAS.</summary>
public enum NasSpaceAnswer
{
    Fits,

    /// <summary>It fits, and the libraries are then over 90 percent of the limit.</summary>
    AlmostFull,

    /// <summary>It would pass the limit or the free space. Nothing is copied.</summary>
    Full,
}

/// <summary>
/// The space check for a library on a NAS. The daemon cannot stop a write that comes over SMB, so the
/// computer checks before it copies.
/// </summary>
public static class NasSpace
{
    /// <summary>The share of the limit above which the NAS counts as almost full.</summary>
    public const double AlmostFullShare = 0.9;

    public static NasSpaceAnswer Check(NasHello hello, long incomingBytes)
    {
        ArgumentNullException.ThrowIfNull(hello);
        var incoming = Math.Max(0, incomingBytes);
        if (incoming > hello.FreeBytes)
            return NasSpaceAnswer.Full;
        if (hello.LimitBytes is not { } limit)
            return NasSpaceAnswer.Fits;
        var after = hello.UsedBytes + incoming;
        if (after > limit)
            return NasSpaceAnswer.Full;
        return after > limit * AlmostFullShare ? NasSpaceAnswer.AlmostFull : NasSpaceAnswer.Fits;
    }
}
