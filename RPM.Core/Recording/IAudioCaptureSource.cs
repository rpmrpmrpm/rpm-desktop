namespace RPM.Core.Recording;

/// <summary>
/// A pull-model audio input: enumerate devices, open one, then poll interleaved float PCM off it. The
/// concrete backend (OpenAL Soft capture) lives in the app project alongside <c>OpenAlSink</c>;
/// <see cref="VinylRecorder"/> depends only on this interface so it can be driven by a fake in tests.
/// </summary>
public interface IAudioCaptureSource : IDisposable
{
    /// <summary>False when no capture backend could be initialised (no device / driver).</summary>
    bool IsAvailable { get; }

    int SampleRate { get; }

    int Channels { get; }

    /// <summary>Input devices the backend can open. The first entry, or one with an empty
    /// <see cref="InputDevice.Id"/>, is the system default.</summary>
    IReadOnlyList<InputDevice> ListDevices();

    /// <summary>Opens the given device (null/empty = default) and begins capturing.</summary>
    void Start(string? deviceId);

    /// <summary>Copies up to <paramref name="maxSamples"/> interleaved float samples that have been
    /// captured since the last read into <paramref name="buffer"/>, returning the count written (0 when
    /// nothing is ready yet). Never blocks.</summary>
    int Read(float[] buffer, int maxSamples);

    /// <summary>Stops capture and releases the device. May be followed by another
    /// <see cref="Start"/>.</summary>
    void Stop();
}
