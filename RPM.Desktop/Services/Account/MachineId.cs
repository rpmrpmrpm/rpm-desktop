using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace RPM.Desktop.Services.Account;

/// <summary>
/// An id for this computer, used as key material for <see cref="SealedFileStore"/>. It is not a
/// secret: any program on the computer can read it. Its value is that it is not stored in the app's
/// folder, so a copy of the folder made elsewhere cannot open the sealed file.
///
/// <list type="bullet">
///   <item>macOS: the hardware UUID, from <c>gethostuuid</c> (the IOPlatformUUID that System Information
///         shows).</item>
///   <item>Windows: <c>MachineGuid</c> under <c>HKLM\SOFTWARE\Microsoft\Cryptography</c>, set when
///         Windows is installed.</item>
///   <item>Linux: <c>/etc/machine-id</c>, or <c>/var/lib/dbus/machine-id</c>.</item>
///   <item>None of those: 32 random bytes kept in the folder. This only means the sealed file is not
///         plain text.</item>
/// </list>
/// </summary>
internal static class MachineId
{
    /// <summary>SHA-256 of the id, with a prefix that names where the id came from.</summary>
    /// <param name="folder">Where the random id is kept when the system has none.</param>
    public static byte[] Read(string folder)
    {
        string? id = null;
        if (OperatingSystem.IsMacOS())
            id = MacHardwareUuid() is { } uuid ? "mac:" + uuid : null;
        else if (OperatingSystem.IsWindows())
            id = WindowsMachineGuid() is { } guid ? "windows:" + guid : null;
        else if (OperatingSystem.IsLinux())
            id = LinuxMachineId() is { } machine ? "linux:" + machine : null;

        return id is not null
            ? SHA256.HashData(Encoding.UTF8.GetBytes(id))
            : Random(folder);
    }

    [SupportedOSPlatform("macos")]
    private static unsafe string? MacHardwareUuid()
    {
        var uuid = stackalloc byte[16];
        // struct timespec { tv_sec; tv_nsec }: wait at most one second for the answer.
        var wait = stackalloc nint[2];
        wait[0] = 1;
        wait[1] = 0;
        return gethostuuid(uuid, wait) == 0
            ? Convert.ToHexString(new ReadOnlySpan<byte>(uuid, 16))
            : null;
    }

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern unsafe int gethostuuid(byte* uuid, nint* wait);

    [SupportedOSPlatform("windows")]
    private static string? WindowsMachineGuid()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string is { Length: > 0 } guid ? guid : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string? LinuxMachineId()
    {
        foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            if (!File.Exists(path))
                continue;
            try
            {
                var text = File.ReadAllText(path).Trim();
                if (text.Length > 0)
                    return text;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    private static byte[] Random(string folder)
    {
        var path = Path.Combine(folder, "machine-id");
        if (File.Exists(path) && File.ReadAllBytes(path) is { Length: 32 } stored)
            return stored;

        var fresh = RandomNumberGenerator.GetBytes(32);
        try
        {
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(folder);
            else
                Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllBytes(path, fresh);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder is not writable. This id then lasts only for this run, so what is sealed now
            // can be opened only in this run. The sealed file's own write fails in the same folder anyway.
        }
        return fresh;
    }
}
