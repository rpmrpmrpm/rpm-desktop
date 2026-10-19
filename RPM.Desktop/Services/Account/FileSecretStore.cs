using System.Security.Cryptography;
using System.Text;

namespace RPM.Desktop.Services.Account;

/// <summary>
/// Secrets as files in one folder: one file per name, each sealed before it is written. The two kinds
/// differ only in the seal: <see cref="DpapiFileStore"/> and <see cref="SealedFileStore"/>.
///
/// <para>A write goes to a temporary file of its own, which is then moved over the old one. So a crash
/// leaves either the old secret or the new one, never half of either. On macOS and Linux the folder is
/// created with mode 0700, and each file with mode 0600, from the moment it exists.</para>
/// </summary>
internal abstract class FileSecretStore : SecretStore
{
    private readonly string _folder;
    private readonly string _extension;

    protected FileSecretStore(string folder, string extension)
    {
        _folder = folder;
        _extension = extension;
    }

    /// <summary>Seals the secret. Throws on failure. The store turns that into a failed write.</summary>
    protected abstract byte[] Seal(string name, byte[] secret);

    /// <summary>Opens what <see cref="Seal"/> wrote. Throws when it cannot.</summary>
    protected abstract byte[] Open(string name, byte[] sealedBytes);

    /// <summary>The name is bound into every seal, so a file renamed to another secret's name does not
    /// open as that secret.</summary>
    protected static byte[] Binding(string name) => Encoding.UTF8.GetBytes("rpm.secret/" + name);

    public override SecretRead Read(string name)
    {
        var path = PathOf(name);
        if (!File.Exists(path))
            return new SecretRead.Absent();

        try
        {
            return new SecretRead.Found(Open(name, File.ReadAllBytes(path)));
        }
        catch (Exception ex)
        {
            return new SecretRead.Failed($"{Name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public override SecretWrite Write(string name, byte[] secret)
    {
        try
        {
            CreateFolder();
            var path = PathOf(name);
            WriteAtomically(path, Seal(name, secret), replace: true);
            return new SecretWrite.Done();
        }
        catch (Exception ex)
        {
            return new SecretWrite.Failed($"{Name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public override SecretWrite Delete(string name)
    {
        var path = PathOf(name);
        if (!File.Exists(path))
            return new SecretWrite.Done();

        try
        {
            File.Delete(path);
            return new SecretWrite.Done();
        }
        catch (Exception ex)
        {
            return new SecretWrite.Failed($"{Name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    protected string Folder => _folder;

    protected void CreateFolder()
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(_folder);
        else
            Directory.CreateDirectory(_folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Writes the bytes under a temporary name that this write owns, then moves them into
    /// place. On macOS and Linux the file is owner-only from the moment it is created.</summary>
    /// <param name="replace">False to keep a file that is already there. The move then throws an
    /// <see cref="IOException"/>, and the existing file stays.</param>
    protected static void WriteAtomically(string path, byte[] bytes, bool replace)
    {
        var temporary = $"{path}.{Environment.ProcessId:x}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: replace);
        }
        catch
        {
            try { File.Delete(temporary); } catch { /* The write already failed. This is only cleanup. */ }
            throw;
        }
    }

    private string PathOf(string name)
    {
        // Names are constants in this app's code. A path separator in one is a bug in the code, not input.
        if (name.Length == 0 || name != Path.GetFileName(name))
            throw new ArgumentException($"Not a secret name: '{name}'", nameof(name));
        return Path.Combine(_folder, name + _extension);
    }
}

/// <summary>
/// The fallback. Files are encrypted with AES-256-CBC and authenticated with HMAC-SHA256 (encrypt,
/// then MAC). The keys are derived from this computer's hardware id and a random salt kept beside
/// the files.
///
/// <para><b>Why not AES-GCM.</b> On macOS, .NET runs AES-GCM through CryptoKit, which is written in
/// Swift. The Mac build is linked with Apple's classic linker (see <c>RPM.Desktop.csproj</c>), because
/// the modern linker cannot link the app. The classic linker cannot link the Swift part, and it stops
/// with "Assertion failed: (it != _dylibToOrdinal.end())". A small test program that calls only
/// <c>AesGcm</c> fails the same way. AES-CBC, HMAC and HKDF come from CommonCrypto, which links with
/// either linker.</para>
///
/// <para><b>What this protects against, and what it does not.</b> A copy of the folder, in a backup, in
/// a synced folder, or on a disk taken out of the machine, cannot be opened without this computer's id,
/// which is not in the folder. A program running as the same user on this computer can read the id and
/// the salt, and so can open the file. The keychain and DPAPI are the stores for that case. This store
/// is used only where neither can be used.</para>
///
/// <para>It is used by every Debug build on the Mac (which has no keychain entitlement), by any Mac build
/// without the provisioning profile, on Linux, and on a Windows profile where DPAPI does not work.</para>
/// </summary>
internal sealed class SealedFileStore : FileSecretStore
{
    private const byte Version = 1;
    private const int IvSize = 16;
    private const int TagSize = 32;
    private static readonly byte[] KeyInfo = Encoding.UTF8.GetBytes("rpm.secrets.sealed-file.v1");

    private readonly byte[] _machine;

    /// <param name="folder">Where the files and the salt are kept.</param>
    /// <param name="machine">This computer's id, from <see cref="MachineId.Read"/>. It is passed in so
    /// a test can stand in for a second computer.</param>
    public SealedFileStore(string folder, byte[] machine)
        : base(folder, ".sealed")
    {
        _machine = machine;
    }

    public override string Name => "sealed file";

    private string SaltPath => Path.Combine(Folder, "salt");

    /// <summary>Version, IV, tag, ciphertext. The tag covers the secret's name, the version, the IV and
    /// the ciphertext.</summary>
    protected override byte[] Seal(string name, byte[] secret)
    {
        var (encryption, authentication) = Keys(createSalt: true);
        var iv = RandomNumberGenerator.GetBytes(IvSize);
        byte[] cipher;
        using (var aes = Aes.Create())
        {
            aes.Key = encryption;
            cipher = aes.EncryptCbc(secret, iv);
        }

        var sealedBytes = new byte[1 + IvSize + TagSize + cipher.Length];
        sealedBytes[0] = Version;
        iv.CopyTo(sealedBytes, 1);
        cipher.CopyTo(sealedBytes, 1 + IvSize + TagSize);
        Tag(authentication, name, iv, cipher).CopyTo(sealedBytes, 1 + IvSize);
        return sealedBytes;
    }

    protected override byte[] Open(string name, byte[] sealedBytes)
    {
        if (sealedBytes.Length < 1 + IvSize + TagSize + 16 || sealedBytes[0] != Version)
            throw new InvalidDataException("not a sealed secret of a version this build reads");

        var (encryption, authentication) = Keys(createSalt: false);
        var iv = sealedBytes.AsSpan(1, IvSize).ToArray();
        var tag = sealedBytes.AsSpan(1 + IvSize, TagSize);
        var cipher = sealedBytes.AsSpan(1 + IvSize + TagSize).ToArray();

        // Check the tag first. Nothing is decrypted unless it matches. A file sealed on another
        // computer, under another name, or changed on disk stops here.
        if (!CryptographicOperations.FixedTimeEquals(tag, Tag(authentication, name, iv, cipher)))
            throw new CryptographicException("the sealed secret does not open here: another computer, another name, or changed on disk");

        using var aes = Aes.Create();
        aes.Key = encryption;
        return aes.DecryptCbc(cipher, iv);
    }

    /// <summary>HMAC-SHA256 over the name's length and bytes, the version, the IV and the ciphertext.
    /// The length comes first, so that two different names and ciphertexts cannot give the same input.</summary>
    private static byte[] Tag(byte[] key, string name, byte[] iv, byte[] cipher)
    {
        var binding = Binding(name);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, binding.Length);
        hmac.AppendData(length);
        hmac.AppendData(binding);
        hmac.AppendData([Version]);
        hmac.AppendData(iv);
        hmac.AppendData(cipher);
        return hmac.GetHashAndReset();
    }

    /// <summary>HKDF over the machine id, salted with this folder's salt. It makes 64 bytes: the first
    /// half encrypts and the second half authenticates. The first write makes the salt. A read with no
    /// salt fails instead of making one, because a new salt cannot open anything sealed before it.</summary>
    private (byte[] Encryption, byte[] Authentication) Keys(bool createSalt)
    {
        byte[] salt;
        if (File.Exists(SaltPath))
        {
            salt = File.ReadAllBytes(SaltPath);
            if (salt.Length != 32)
                throw new InvalidDataException("the salt file is not 32 bytes");
        }
        else if (createSalt)
        {
            salt = RandomNumberGenerator.GetBytes(32);
            try
            {
                WriteAtomically(SaltPath, salt, replace: false);
            }
            catch (IOException) when (File.Exists(SaltPath))
            {
                // Another writer made a salt first. Every sealed file is read with that salt, so use it.
                // Replacing it would make those files unreadable.
                salt = File.ReadAllBytes(SaltPath);
                if (salt.Length != 32)
                    throw new InvalidDataException("the salt file is not 32 bytes");
            }
        }
        else
        {
            throw new FileNotFoundException("the salt file is missing");
        }

        var keys = HKDF.DeriveKey(HashAlgorithmName.SHA256, _machine, outputLength: 64, salt, KeyInfo);
        return (keys[..32], keys[32..]);
    }
}
