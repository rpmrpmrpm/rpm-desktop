using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using RPM.Core.Recording;
// InputDevice here means a sound card's input. The Uno SDK adds System.Windows.Input to the implicit
// usings. On the WinUI head that namespace has its own InputDevice type, so this alias is needed to
// avoid an ambiguous name.
using InputDevice = RPM.Core.Recording.InputDevice;

namespace RPM.Desktop.Services.Audio;

/// <summary>The audio input side of the pipeline: OpenAL Soft's capture API. It is the counterpart of
/// the playback in <see cref="OpenAlSink"/>. One code path serves macOS, Windows and Linux. OpenAL Soft
/// uses CoreAudio, WASAPI or ALSA underneath.
///
/// <para>The Silk.NET.OpenAL package does not expose the ALC capture entry points. They are in an
/// extension package that this project does not reference. So this class declares the six
/// <c>alcCapture*</c> functions itself. The native library is the one that Silk already deploys and
/// loads (<c>libopenal</c> or <c>soft_oal</c>). It is found by name on each platform, so no extra
/// dependency is needed.</para>
///
/// <para>Capture does not need an AL context. <c>alcCaptureOpenDevice</c> owns its own ring buffer.
/// <see cref="Read"/> takes all the samples that the device has collected since the last call.</para>
/// </summary>
public sealed unsafe partial class OpenAlCapture : IAudioCaptureSource
{
    private const int AlcCaptureSamples = 0x312;
    private const int AlcCaptureDeviceSpecifier = 0x310;
    private const int AlcCaptureDefaultDeviceSpecifier = 0x311;
    private const int AlFormatStereo16 = 0x1103;

    public int SampleRate => 44100;
    public int Channels => 2;

    private nint _device;
    private short[] _scratch = Array.Empty<short>();

    public bool IsAvailable { get; }

    public OpenAlCapture()
    {
        try
        {
            // Reading the default device name checks that the library loaded and that the ALC capture
            // symbols exist. An empty device list is still a usable state, so it is not treated as a
            // failure.
            _ = ReadAlcString(0, AlcCaptureDefaultDeviceSpecifier);
            IsAvailable = true;
        }
        catch { IsAvailable = false; }
    }

    public IReadOnlyList<InputDevice> ListDevices()
    {
        var devices = new List<InputDevice>();
        try
        {
            string? def = ReadAlcString(0, AlcCaptureDefaultDeviceSpecifier);
            foreach (var name in ReadAlcStringList(0, AlcCaptureDeviceSpecifier))
            {
                bool isDefault = name == def;
                devices.Add(new InputDevice(name, isDefault ? Strings.Loc.Format("Vinyl_InputIsDefault", name) : name));
            }
            // The system default is always offered, even when the list of devices is empty.
            if (devices.Count == 0)
                devices.Add(new InputDevice("", string.IsNullOrEmpty(def) ? Strings.Loc.Get("Vinyl_InputSystemDefault") : def));
        }
        catch { /* leave whatever we gathered */ }
        return devices;
    }

    public void Start(string? deviceId)
    {
        Stop();

        // A ring of about one second at stereo 44.1 kHz. It is large enough that a late read never
        // overflows it.
        int ringFrames = SampleRate;
        if (string.IsNullOrEmpty(deviceId))
        {
            _device = alcCaptureOpenDevice(null, (uint)SampleRate, AlFormatStereo16, ringFrames);
        }
        else
        {
            var bytes = Encoding.UTF8.GetBytes(deviceId + '\0');
            fixed (byte* p = bytes)
                _device = alcCaptureOpenDevice(p, (uint)SampleRate, AlFormatStereo16, ringFrames);
        }

        if (_device == 0)
            throw new InvalidOperationException("could not open the audio input device");

        alcCaptureStart(_device);
    }

    public int Read(float[] buffer, int maxSamples)
    {
        if (_device == 0) return 0;

        int available = 0;
        alcGetIntegerv(_device, AlcCaptureSamples, 1, &available);   // number of available frames
        if (available <= 0) return 0;

        int maxFrames = maxSamples / Channels;
        int frames = Math.Min(available, maxFrames);
        if (frames <= 0) return 0;

        int samples = frames * Channels;
        if (_scratch.Length < samples) _scratch = new short[samples];

        fixed (short* p = _scratch)
            alcCaptureSamples(_device, p, frames);

        for (int i = 0; i < samples; i++)
            buffer[i] = _scratch[i] / 32768f;

        return samples;
    }

    public void Stop()
    {
        if (_device == 0) return;
        try { alcCaptureStop(_device); } catch { }
        try { alcCaptureCloseDevice(_device); } catch { }
        _device = 0;
    }

    public void Dispose() => Stop();

    private static string? ReadAlcString(nint device, int token)
    {
        byte* p = alcGetString(device, token);
        return p == null ? null : Marshal.PtrToStringUTF8((nint)p);
    }

    /// <summary>Reads an ALC device list. Entries are separated by nulls, and the list ends with an
    /// empty entry.</summary>
    private static IReadOnlyList<string> ReadAlcStringList(nint device, int token)
    {
        var list = new List<string>();
        byte* p = alcGetString(device, token);
        if (p == null) return list;

        int i = 0;
        while (true)
        {
            int start = i;
            while (p[i] != 0) i++;
            if (i == start) break;            // an empty entry marks the end of the list
            list.Add(Marshal.PtrToStringUTF8((nint)(p + start), i - start));
            i++;                              // skip the separating null
        }
        return list;
    }

    // ----- native binding -------------------------------------------------------------------

    private const string Lib = "rpm-openal";

    static OpenAlCapture()
    {
        NativeLibrary.SetDllImportResolver(typeof(OpenAlCapture).Assembly, Resolve);
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Lib) return 0;
        // This is the same binary that Silk deploys under runtimes/<rid>/native. Its file name differs
        // by platform.
        string[] candidates = OperatingSystem.IsWindows()
            ? ["soft_oal", "OpenAL32", "OpenAL"]
            : OperatingSystem.IsMacOS()
                ? ["libopenal.dylib", "libopenal", "openal", "OpenAL"]
                : ["libopenal.so", "libopenal.so.1", "libopenal", "openal"];
        foreach (var candidate in candidates)
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
                return handle;
        return 0;
    }

    [DllImport(Lib, EntryPoint = "alcCaptureOpenDevice")]
    private static extern nint alcCaptureOpenDevice(byte* deviceName, uint frequency, int format, int bufferSizeFrames);

    [DllImport(Lib, EntryPoint = "alcCaptureCloseDevice")]
    private static extern bool alcCaptureCloseDevice(nint device);

    [DllImport(Lib, EntryPoint = "alcCaptureStart")]
    private static extern void alcCaptureStart(nint device);

    [DllImport(Lib, EntryPoint = "alcCaptureStop")]
    private static extern void alcCaptureStop(nint device);

    [DllImport(Lib, EntryPoint = "alcCaptureSamples")]
    private static extern void alcCaptureSamples(nint device, short* buffer, int frames);

    [DllImport(Lib, EntryPoint = "alcGetIntegerv")]
    private static extern void alcGetIntegerv(nint device, int token, int size, int* values);

    [DllImport(Lib, EntryPoint = "alcGetString")]
    private static extern byte* alcGetString(nint device, int token);
}
