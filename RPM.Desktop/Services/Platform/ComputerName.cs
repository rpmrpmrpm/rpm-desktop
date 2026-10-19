using System.Runtime.InteropServices;

namespace RPM.Desktop.Services.Platform;

/// <summary>The name of this computer, as its owner would recognise it.
///
/// <para><b>Why this does not use <see cref="Environment.MachineName"/>.</b> On macOS that returns the
/// POSIX hostname. The user does not choose the hostname. It follows DHCP. A FRITZ!Box can hand out a name
/// that is a bare UUID. macOS adopts it, and this app then showed the machine as
/// <c>2571107a-0470-46da-a09e-10af215c9400</c>, in the settings box and on the network, to every neighbour
/// who could see the beacon. The name the user sets is the <i>computer name</i>. It is a different field.
/// macOS shows it in AirDrop and the Finder sidebar, and it is the name a person picks for the
/// machine.</para>
///
/// <para>The name is read once. It is a setting on the machine, changed in System Settings. Reading it on
/// every beacon would be a framework call on the announcing thread, for an answer that does not
/// change.</para>
/// </summary>
public static class ComputerName
{
    private const string SystemConfiguration =
        "/System/Library/Frameworks/SystemConfiguration.framework/SystemConfiguration";
    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>kCFStringEncodingUTF8.</summary>
    private const uint Utf8 = 0x08000100;

    [DllImport(SystemConfiguration)]
    private static extern nint SCDynamicStoreCopyComputerName(nint store, nint nameEncoding);

    [DllImport(CoreFoundation)]
    private static extern bool CFStringGetCString(nint theString, byte[] buffer, long size, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint cf);

    private static readonly Lazy<string> Resolved = new(Read, isThreadSafe: true);

    /// <summary>The name to show and to announce. Never empty. The hostname is the fallback, and on Windows
    /// and Linux it is the whole answer.</summary>
    public static string Mine => Resolved.Value;

    private static string Read()
    {
        var hostname = Environment.MachineName;

        if (!OperatingSystem.IsMacOS())
            return hostname;

        try
        {
            // A null store asks the running configuration daemon directly. That suits a one-off read,
            // because no session needs to stay open.
            var name = SCDynamicStoreCopyComputerName(0, 0);
            if (name == 0)
                return hostname;

            try
            {
                var buffer = new byte[512];
                if (!CFStringGetCString(name, buffer, buffer.Length, Utf8))
                    return hostname;

                var end = Array.IndexOf(buffer, (byte)0);
                var text = System.Text.Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
                return string.IsNullOrWhiteSpace(text) ? hostname : text;
            }
            finally
            {
                CFRelease(name);
            }
        }
        catch
        {
            // The framework may fail to load, or the name may not convert. The hostname is a less friendly
            // name, but it is a good fallback. This must not fail app launch over the computer's name.
            return hostname;
        }
    }
}
