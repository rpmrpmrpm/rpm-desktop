namespace RPM.Core.Recording;

/// <summary>What an input device appears to be, judged from the only thing a driver gives us: its name.
/// Drives whether speaker monitoring is safe to switch on by default.</summary>
public enum InputDeviceKind
{
    /// <summary>Cannot be told from the name. Treated as unsafe to monitor, because plenty of USB
    /// gadgets ("USB Audio CODEC") are turntables and plenty are microphones.</summary>
    Unknown,
    /// <summary>A microphone. Monitoring it through speakers feeds back.</summary>
    Microphone,
    /// <summary>A line-level input (line-in, phono, turntable). Safe to monitor, because it cannot pick
    /// the speakers back up.</summary>
    LineIn,
}

/// <summary>
/// Name-based classification of audio inputs. The point is one decision: may we play the input back
/// through the speakers without creating a feedback loop? Only a positively line-level input earns a
/// yes. A microphone does not, and neither does anything that cannot be identified. The cost of being
/// wrong is feedback in the user's room, and a ruined recording of it.
/// </summary>
public static class InputDevices
{
    // Checked first: unambiguous line-level wording. "Built-in Line Input" must not be read as a
    // microphone just because it says "built-in".
    private static readonly string[] LineMarkers =
    [
        "line in", "line-in", "linein", "line input", "line_in",
        "phono", "turntable", "record player", "tape deck", "aux in",
    ];

    private static readonly string[] MicMarkers =
    [
        "microphone", "webcam", "camera", "headset", "airpods", "facetime",
        "handsfree", "hands-free", "built-in", "built in", "internal", "voice",
    ];

    public static InputDeviceKind Classify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return InputDeviceKind.Unknown;

        var n = name.ToLowerInvariant();

        if (LineMarkers.Any(m => n.Contains(m, StringComparison.Ordinal)))
            return InputDeviceKind.LineIn;

        if (MicMarkers.Any(m => n.Contains(m, StringComparison.Ordinal)) || HasMicWord(n))
            return InputDeviceKind.Microphone;

        return InputDeviceKind.Unknown;
    }

    /// <summary>"mic" as a whole word. This matches "Mic 1" and "USB Mic", and does not match "micro"
    /// or any product name that only contains the letters.</summary>
    private static bool HasMicWord(string lower)
    {
        int i = 0;
        while ((i = lower.IndexOf("mic", i, StringComparison.Ordinal)) >= 0)
        {
            bool startOk = i == 0 || !char.IsLetterOrDigit(lower[i - 1]);
            int end = i + 3;
            bool endOk = end >= lower.Length || !char.IsLetterOrDigit(lower[end]);
            if (startOk && endOk) return true;
            i = end;
        }
        return false;
    }

    /// <summary>Whether speaker monitoring should start switched on for this device. Only a known
    /// line-level input qualifies; anything else defaults off and the user can still turn it on (they
    /// may be wearing headphones).</summary>
    public static bool ShouldMonitorByDefault(InputDeviceKind kind) => kind == InputDeviceKind.LineIn;
}
