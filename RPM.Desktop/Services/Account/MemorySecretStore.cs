namespace RPM.Desktop.Services.Account;

/// <summary>
/// A store that keeps its secrets in this process only: empty when it is made, gone when the process
/// ends. A harness run (see <see cref="SessionHome.Harness"/>) uses this store. It must not read the
/// sign-in kept on this computer, and it must not leave one behind either.
/// </summary>
internal sealed class MemorySecretStore : SecretStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, byte[]> _secrets = [];

    public override string Name => "memory store";

    public override SecretRead Read(string name)
    {
        lock (_gate)
            return _secrets.TryGetValue(name, out var bytes)
                ? new SecretRead.Found([.. bytes])
                : new SecretRead.Absent();
    }

    public override SecretWrite Write(string name, byte[] secret)
    {
        lock (_gate)
            _secrets[name] = [.. secret];
        return new SecretWrite.Done();
    }

    public override SecretWrite Delete(string name)
    {
        lock (_gate)
            _secrets.Remove(name);
        return new SecretWrite.Done();
    }
}
