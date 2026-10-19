using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RPM.Desktop.Services.Account;

/// <summary>
/// Secrets as files sealed with DPAPI for the current Windows user. Only the same user on the same
/// Windows installation can open them. Another account on the PC, or a copy of the folder on another
/// PC, sees bytes it cannot open. DPAPI never asks the user anything. The key comes from the Windows
/// sign-in.
///
/// <para>This calls <c>crypt32</c> directly, not the <c>System.Security.Cryptography.ProtectedData</c>
/// package. That package wraps the same two calls. Using it would add one more component to ship and to
/// credit.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class DpapiFileStore : FileSecretStore
{
    public DpapiFileStore(string folder)
        : base(folder, ".dpapi")
    {
    }

    public override string Name => "dpapi";

    /// <summary>Whether DPAPI works for this user. It seals two bytes and opens them again. It writes
    /// nothing. It returns false on a profile whose DPAPI master key cannot be reached.</summary>
    public static bool Works(out string detail)
    {
        var probe = new byte[] { 0xA5, 0x5A };
        if (!TryProtect(probe, Binding("probe"), out var sealedBytes, out var error)
            || !TryUnprotect(sealedBytes, Binding("probe"), out var opened, out error))
        {
            detail = $"DPAPI error {error}";
            return false;
        }

        detail = "";
        return opened.AsSpan().SequenceEqual(probe);
    }

    protected override byte[] Seal(string name, byte[] secret) =>
        TryProtect(secret, Binding(name), out var sealedBytes, out var error)
            ? sealedBytes
            : throw new Win32Exception(error);

    protected override byte[] Open(string name, byte[] sealedBytes) =>
        TryUnprotect(sealedBytes, Binding(name), out var secret, out var error)
            ? secret
            : throw new Win32Exception(error);

    private static bool TryProtect(byte[] plain, byte[] entropy, out byte[] sealedBytes, out int error)
    {
        fixed (byte* pPlain = plain)
        fixed (byte* pEntropy = entropy)
        {
            var input = new DataBlob { Size = plain.Length, Data = pPlain };
            var extra = new DataBlob { Size = entropy.Length, Data = pEntropy };
            var output = default(DataBlob);
            if (CryptProtectData(&input, null, &extra, 0, 0, UiForbidden, &output) == 0)
            {
                error = Marshal.GetLastPInvokeError();
                sealedBytes = [];
                return false;
            }

            sealedBytes = TakeAndFree(output);
            error = 0;
            return true;
        }
    }

    private static bool TryUnprotect(byte[] sealedBytes, byte[] entropy, out byte[] plain, out int error)
    {
        fixed (byte* pSealed = sealedBytes)
        fixed (byte* pEntropy = entropy)
        {
            var input = new DataBlob { Size = sealedBytes.Length, Data = pSealed };
            var extra = new DataBlob { Size = entropy.Length, Data = pEntropy };
            var output = default(DataBlob);
            if (CryptUnprotectData(&input, null, &extra, 0, 0, UiForbidden, &output) == 0)
            {
                error = Marshal.GetLastPInvokeError();
                plain = [];
                return false;
            }

            plain = TakeAndFree(output);
            error = 0;
            return true;
        }
    }

    /// <summary>Copies what DPAPI allocated, then gives the allocation back with LocalFree.</summary>
    private static byte[] TakeAndFree(DataBlob blob)
    {
        try
        {
            return new ReadOnlySpan<byte>(blob.Data, blob.Size).ToArray();
        }
        finally
        {
            LocalFree((nint)blob.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public byte* Data;
    }

    /// <summary>CRYPTPROTECT_UI_FORBIDDEN: fail rather than show a prompt.</summary>
    private const int UiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern int CryptProtectData(
        DataBlob* dataIn, char* description, DataBlob* entropy, nint reserved, nint prompt, int flags, DataBlob* dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern int CryptUnprotectData(
        DataBlob* dataIn, char** description, DataBlob* entropy, nint reserved, nint prompt, int flags, DataBlob* dataOut);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
