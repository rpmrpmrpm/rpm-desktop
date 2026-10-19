using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace RPM.Desktop.Services.Account;

/// <summary>
/// Secrets in the macOS data protection keychain, as generic password items under the service
/// <c>com.rpm.player.desktop</c>, with the secret's name as the account.
///
/// <para>The service is not <c>com.rpm.player</c>. The iPhone app keeps its own session under that
/// service, with the same account name, in its own format. Both apps share one App ID and one keychain
/// group. If this store used that service, an iPhone app run on an Apple silicon Mac could read and
/// replace this app's session.</para>
///
/// <para><b>Why the data protection keychain and not the login keychain.</b> The login keychain decides
/// access per binary. An item records the Developer ID build that wrote it. A second Developer ID build
/// of the same code then has to ask for the user's password to read that item. For rpm that would be a
/// password prompt after every update. The data protection keychain does not ask. Access follows the
/// app's <c>keychain-access-groups</c> entitlement, so every build that carries it reads the same items
/// without a prompt.</para>
///
/// <para><b>The entitlement.</b> It is restricted. A Developer ID app may carry it only together with an
/// embedded provisioning profile that allows it. Without the profile, the system kills the app at
/// launch. <c>scripts/release-mac.sh</c> embeds the profile for <c>com.rpm.player</c> and signs the
/// entitlement onto the app (step 3c). A build without it gets <c>errSecMissingEntitlement</c> when it
/// writes. That includes every Debug build and every unsigned bundle. <see cref="Works"/> is how
/// <see cref="SessionStore.For"/> finds this out, and then it uses the sealed file instead.</para>
///
/// <para>Items use <c>kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly</c>. They can be read once the Mac
/// has been unlocked after a restart. They are never synced through iCloud, and never restored onto
/// another Mac.</para>
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class MacKeychainStore : SecretStore
{
    private const string Service = "com.rpm.player.desktop";

    public override string Name => "keychain";

    /// <summary>
    /// Whether this build can use the data protection keychain. It adds a probe item and then deletes it.
    /// A build without the entitlement is refused the add with <c>errSecMissingEntitlement</c>.
    ///
    /// <para>The probe has to write. If it only asked for an item that does not exist, the keychain would
    /// answer a build without the entitlement as if the item were simply not there. That is the same
    /// answer an entitled build gets. So a write is the only test that tells them apart.</para>
    /// </summary>
    /// <param name="status">The keychain's answer to the add, for the log.</param>
    public static bool Works(out int status)
    {
        const string probe = "rpm.keychain-probe";
        try
        {
            var item = Query(probe, returnData: false);
            var value = CFData([0xA5]);
            try
            {
                CFDictionaryAddValue(item, KSecValueData, value);
                CFDictionaryAddValue(item, KSecAttrAccessible, KSecAttrAccessibleAfterFirstUnlockThisDeviceOnly);
                status = SecItemAdd(item, null);
            }
            finally
            {
                CFRelease(value);
                CFRelease(item);
            }

            // A probe item can be left behind by a launch that stopped between the add and the delete.
            // In that case the add was allowed, so it counts as success.
            if (status == ErrSecDuplicateItem)
                status = ErrSecSuccess;

            if (status == ErrSecSuccess)
            {
                var query = Query(probe, returnData: false);
                try { SecItemDelete(query); }
                finally { CFRelease(query); }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        {
            // The framework or one of its symbols is missing. This is not a Mac that the app runs on.
            status = int.MinValue;
            return false;
        }

        return status == ErrSecSuccess;
    }

    public override SecretRead Read(string name)
    {
        var query = Query(name, returnData: true);
        nint data = 0;
        int status;
        try { status = SecItemCopyMatching(query, &data); }
        finally { CFRelease(query); }

        if (status == ErrSecItemNotFound)
            return new SecretRead.Absent();
        if (status != ErrSecSuccess)
            return new SecretRead.Failed($"SecItemCopyMatching answered {status}");

        try
        {
            var length = (int)CFDataGetLength(data);
            var bytes = new byte[length];
            if (length > 0)
                Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, length);
            return new SecretRead.Found(bytes);
        }
        finally
        {
            CFRelease(data);
        }
    }

    public override SecretWrite Write(string name, byte[] secret)
    {
        // Update first, and add only when there is nothing to update. Deleting and then adding would leave
        // a moment with no secret stored. A crash in that moment would lose the secret.
        var query = Query(name, returnData: false);
        var value = CFData(secret);
        var change = NewDictionary();
        int status;
        try
        {
            CFDictionaryAddValue(change, KSecValueData, value);
            status = SecItemUpdate(query, change);
        }
        finally
        {
            CFRelease(change);
            CFRelease(query);
        }

        if (status == ErrSecItemNotFound)
        {
            var item = Query(name, returnData: false);
            try
            {
                CFDictionaryAddValue(item, KSecValueData, value);
                CFDictionaryAddValue(item, KSecAttrAccessible, KSecAttrAccessibleAfterFirstUnlockThisDeviceOnly);
                status = SecItemAdd(item, null);
            }
            finally
            {
                CFRelease(item);
            }
        }

        CFRelease(value);
        return status == ErrSecSuccess
            ? new SecretWrite.Done()
            : new SecretWrite.Failed($"keychain write answered {status}");
    }

    public override SecretWrite Delete(string name)
    {
        var query = Query(name, returnData: false);
        int status;
        try { status = SecItemDelete(query); }
        finally { CFRelease(query); }

        return status is ErrSecSuccess or ErrSecItemNotFound
            ? new SecretWrite.Done()
            : new SecretWrite.Failed($"SecItemDelete answered {status}");
    }

    /// <summary>The dictionary that names one item. The caller releases it.</summary>
    private static nint Query(string name, bool returnData)
    {
        var dictionary = NewDictionary();
        var service = CFString(Service);
        var account = CFString(name);

        CFDictionaryAddValue(dictionary, KSecClass, KSecClassGenericPassword);
        CFDictionaryAddValue(dictionary, KSecAttrService, service);
        CFDictionaryAddValue(dictionary, KSecAttrAccount, account);
        CFDictionaryAddValue(dictionary, KSecUseDataProtectionKeychain, KCFBooleanTrue);
        if (returnData)
        {
            CFDictionaryAddValue(dictionary, KSecReturnData, KCFBooleanTrue);
            CFDictionaryAddValue(dictionary, KSecMatchLimit, KSecMatchLimitOne);
        }

        // The dictionary keeps its own reference to the values it holds.
        CFRelease(service);
        CFRelease(account);
        return dictionary;
    }

    private static nint NewDictionary() =>
        CFDictionaryCreateMutable(0, 0, KCFTypeDictionaryKeyCallBacks, KCFTypeDictionaryValueCallBacks);

    private static nint CFString(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        fixed (byte* p = bytes)
            return CFStringCreateWithBytes(0, p, bytes.Length, KCFStringEncodingUtf8, 0);
    }

    private static nint CFData(byte[] bytes)
    {
        fixed (byte* p = bytes)
            return CFDataCreate(0, p, bytes.Length);
    }

    // ── Security.framework and CoreFoundation ──────────────────────────────────────────────────
    //
    // Every signature is blittable, so Native AOT needs no marshalling for any of them.

    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecDuplicateItem = -25299;
    private const uint KCFStringEncodingUtf8 = 0x08000100;

    [DllImport(Security)]
    private static extern int SecItemCopyMatching(nint query, nint* result);

    [DllImport(Security)]
    private static extern int SecItemAdd(nint attributes, nint* result);

    [DllImport(Security)]
    private static extern int SecItemUpdate(nint query, nint attributesToUpdate);

    [DllImport(Security)]
    private static extern int SecItemDelete(nint query);

    [DllImport(CoreFoundation)]
    private static extern nint CFStringCreateWithBytes(nint allocator, byte* bytes, nint length, uint encoding, byte isExternal);

    [DllImport(CoreFoundation)]
    private static extern nint CFDataCreate(nint allocator, byte* bytes, nint length);

    [DllImport(CoreFoundation)]
    private static extern nint CFDataGetLength(nint data);

    [DllImport(CoreFoundation)]
    private static extern nint CFDataGetBytePtr(nint data);

    [DllImport(CoreFoundation)]
    private static extern nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keyCallBacks, nint valueCallBacks);

    [DllImport(CoreFoundation)]
    private static extern void CFDictionaryAddValue(nint dictionary, nint key, nint value);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint cf);

    // The kSec* and kCFBoolean* symbols are pointer-typed globals. The export is the address of the
    // variable, and the variable holds the object. The two callback tables are struct globals. The
    // export is the table itself, and CoreFoundation expects its address.
    private static readonly nint KSecClass = Global(Security, "kSecClass");
    private static readonly nint KSecClassGenericPassword = Global(Security, "kSecClassGenericPassword");
    private static readonly nint KSecAttrService = Global(Security, "kSecAttrService");
    private static readonly nint KSecAttrAccount = Global(Security, "kSecAttrAccount");
    private static readonly nint KSecAttrAccessible = Global(Security, "kSecAttrAccessible");
    private static readonly nint KSecAttrAccessibleAfterFirstUnlockThisDeviceOnly =
        Global(Security, "kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly");
    private static readonly nint KSecValueData = Global(Security, "kSecValueData");
    private static readonly nint KSecReturnData = Global(Security, "kSecReturnData");
    private static readonly nint KSecMatchLimit = Global(Security, "kSecMatchLimit");
    private static readonly nint KSecMatchLimitOne = Global(Security, "kSecMatchLimitOne");
    private static readonly nint KSecUseDataProtectionKeychain = Global(Security, "kSecUseDataProtectionKeychain");
    private static readonly nint KCFBooleanTrue = Global(CoreFoundation, "kCFBooleanTrue");
    private static readonly nint KCFTypeDictionaryKeyCallBacks = Table(CoreFoundation, "kCFTypeDictionaryKeyCallBacks");
    private static readonly nint KCFTypeDictionaryValueCallBacks = Table(CoreFoundation, "kCFTypeDictionaryValueCallBacks");

    private static nint Global(string library, string symbol) =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(library), symbol));

    private static nint Table(string library, string symbol) =>
        NativeLibrary.GetExport(NativeLibrary.Load(library), symbol);
}
