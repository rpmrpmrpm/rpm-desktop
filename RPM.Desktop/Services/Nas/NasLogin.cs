using System.Text.Json;
using System.Text.Json.Serialization;
using RPM.Desktop.Services.Account;

namespace RPM.Desktop.Services.Nas;

/// <summary>A NAS user name and password. <see cref="ToString"/> leaves the password out, so a log line or
/// an exception message that prints this record cannot carry it.</summary>
public sealed record NasCredentials(string User, string Password)
{
    public override string ToString() => $"NasCredentials {{ User = {User} }}";
}

/// <summary>
/// The NAS login, kept in the store the account session uses (the Keychain on macOS, a file sealed with
/// the system's data protection elsewhere). It is read only to mount the share and is never logged.
/// </summary>
public sealed class NasLogin(SecretStore store)
{
    private static string Name(string nasDeviceId) => "nas-login:" + nasDeviceId;

    public bool Save(string nasDeviceId, NasCredentials login)
    {
        ArgumentNullException.ThrowIfNull(login);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Stored(login.User, login.Password), NasLoginJson.Default.Stored);
        return store.Write(Name(nasDeviceId), bytes) is SecretWrite.Done;
    }

    public NasCredentials? Read(string nasDeviceId)
    {
        if (store.Read(Name(nasDeviceId)) is not SecretRead.Found found)
            return null;
        try
        {
            return JsonSerializer.Deserialize(found.Bytes, NasLoginJson.Default.Stored) is { } stored
                ? new NasCredentials(stored.User, stored.Password)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Delete(string nasDeviceId) => store.Delete(Name(nasDeviceId));

    internal sealed record Stored(string User, string Password);
}

[JsonSerializable(typeof(NasLogin.Stored))]
internal sealed partial class NasLoginJson : JsonSerializerContext;
