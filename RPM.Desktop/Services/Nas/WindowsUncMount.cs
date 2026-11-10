using System.Runtime.InteropServices;
using RPM.Core.Nas;

namespace RPM.Desktop.Services.Nas;

/// <summary>
/// Windows: <c>WNetAddConnection2</c> to <c>\\host\share</c> with no local name, so no drive letter is
/// taken. The library is then reached through its UNC path.
/// </summary>
internal sealed unsafe class WindowsUncMount : NasMount
{
    private const int ResourceTypeDisk = 1;
    private const int ErrorSessionCredentialConflict = 1219;
    private const int ErrorLogonFailure = 1326;
    private const int ErrorInvalidPassword = 86;
    private const int ErrorAlreadyAssigned = 85;

    protected override NasMountResult MountCore(string nasDeviceId, NasSmbAddress address, NasCredentials login)
    {
        var share = address.UncShare;
        if (Directory.Exists(share))
            return new NasMountResult.Mounted(share);

        var remote = Marshal.StringToHGlobalUni(share);
        var user = Marshal.StringToHGlobalUni(login.User);
        var password = Marshal.StringToHGlobalUni(login.Password);
        int status;
        try
        {
            var resource = new NetResource { Type = ResourceTypeDisk, RemoteName = remote };
            status = WNetAddConnection2W(&resource, password, user, 0);
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(password);
            Marshal.FreeHGlobal(user);
            Marshal.FreeHGlobal(remote);
        }

        // 1219: Windows already holds a connection to this server, made with another login. The share is
        // still reachable through it.
        if (status is 0 or ErrorAlreadyAssigned or ErrorSessionCredentialConflict && Directory.Exists(share))
            return new NasMountResult.Mounted(share);
        if (status is ErrorLogonFailure or ErrorInvalidPassword)
            return new NasMountResult.WrongLogin();
        return new NasMountResult.Unreachable($"WNetAddConnection2 answered {status}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public nint LocalName;
        public nint RemoteName;
        public nint Comment;
        public nint Provider;
    }

    [DllImport("mpr.dll")]
    private static extern int WNetAddConnection2W(NetResource* resource, nint password, nint user, int flags);
}
