#if DEBUG
using System.Diagnostics;
using RPM.Core.Recording;
// InputDevice means the sound card's input here. Uno's SDK adds System.Windows.Input to the
// implicit usings, and on the WinUI head that namespace has an InputDevice of its own, so the
// name is ambiguous without this.
using InputDevice = RPM.Core.Recording.InputDevice;

namespace RPM.Desktop.Presentation.Gallery;

/// <summary> A turntable that is not there: the capture source that the vinyl wizard runs on in the
/// gallery.
///
/// <para>The window is almost all live signal: two peak meters, a gain slider read against them, and a
/// clip light. On a machine with nothing plugged in there is no signal at all, so none of this could be
/// seen. This source stands in for the input. It makes a few bars of tone, paced by the wall clock so
/// the meters move at the rate they would from a real input. A slow swell is added, so the level is
/// something to watch rather than a fixed bar.</para>
///
/// <para>Two devices are offered. The second one matters: <see cref="InputDevices.Classify"/> reads
/// "Microphone" in the name, and the dialog then turns monitoring off and shows the reason on screen.
/// So the state that only appears on other people's hardware is one click away here.</para>
///
/// <para>Recording is real. A side captured from here goes through the same encoder to the same FLAC
/// file, in the same session folder. That is the point. The recorded-sides list in the gallery is the
/// list the app builds, not a drawing of one.</para> </summary>
internal sealed partial class GalleryCapture : IAudioCaptureSource
{
    public bool IsAvailable => true;

    public int SampleRate => 44100;

    public int Channels => 2;

    private static readonly IReadOnlyList<InputDevice> Devices =
    [
        new InputDevice("gallery-line", "Gallery Turntable (Line In)"),
        new InputDevice("gallery-mic", "Gallery Microphone"),
    ];

    public IReadOnlyList<InputDevice> ListDevices() => Devices;

    private readonly Stopwatch _clock = new();
    private long _framesRead;
    private volatile bool _running;

    public void Start(string? deviceId)
    {
        _framesRead = 0;
        _clock.Restart();
        _running = true;
    }

    public void Stop()
    {
        _running = false;
        _clock.Stop();
    }

    /// <summary>Hands over exactly as many frames as have come due since the last read, and no more.
    /// The pump sleeps when this returns nothing. So the clock here is what keeps a recorded side's
    /// length correct. A source that answered every read in full would write twenty minutes of audio in
    /// a couple of seconds.</summary>
    public int Read(float[] buffer, int maxSamples)
    {
        if (!_running)
            return 0;

        long due = (long)(_clock.Elapsed.TotalSeconds * SampleRate);
        int frames = (int)Math.Min(due - _framesRead, maxSamples / Channels);
        if (frames <= 0)
            return 0;

        for (int i = 0; i < frames; i++)
        {
            float s = Sample((_framesRead + i) / (double)SampleRate);
            buffer[i * Channels] = s;
            buffer[i * Channels + 1] = s;
        }

        _framesRead += frames;
        return frames * Channels;
    }

    /// <summary>A kick, a bass line and a pad, under a slow swell. It is composed for a meter rather
    /// than an ear. The kick gives the bars a pulse to follow, and the swell moves the peak up and down
    /// over eight seconds, so the gain slider visibly changes where it lands.</summary>
    private static float Sample(double t)
    {
        double beat = t * 2.0;                                  // 120 bpm
        double intoBeat = beat - Math.Floor(beat);

        double kickEnv = Math.Exp(-intoBeat * 9.0);
        double kick = Math.Sin(2 * Math.PI * (55 + 40 * kickEnv) * t) * kickEnv * 0.55;

        double[] bassHz = [82.4, 82.4, 110.0, 92.5];
        double bass = Math.Sin(2 * Math.PI * bassHz[(int)(beat / 2) % 4] * t) * 0.20;

        double pad = (Math.Sin(2 * Math.PI * 329.6 * t)
                    + Math.Sin(2 * Math.PI * 415.3 * t)
                    + Math.Sin(2 * Math.PI * 493.9 * t)) * 0.06;

        // Half to full over eight seconds and back, so the meter is never sitting still.
        double swell = 0.5 + 0.5 * Math.Sin(2 * Math.PI * t / 8.0);

        return (float)Math.Clamp((kick + bass + pad) * (0.55 + 0.45 * swell), -1.0, 1.0);
    }

    public void Dispose() => Stop();
}
#endif
