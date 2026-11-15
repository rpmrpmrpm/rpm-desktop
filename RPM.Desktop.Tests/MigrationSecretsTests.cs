using RPM.Core.Migration;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Services.Migration;

namespace RPM.Desktop.Tests;

/// <summary>The kept sign-ins of unfinished imports: written at the start, rewritten when iBroadcast
/// renews its tokens, deleted when the import finishes or is forgotten.</summary>
public class MigrationSecretsTests
{
    private const string Name = "migration:ibroadcast:7654321";
    private static readonly IBroadcastAccount Account = new("7654321", "testlistener", true);
    private static readonly IBroadcastGrant First = new("access-1", "refresh-1", new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private static readonly IBroadcastGrant Second = new("access-2", "refresh-2", new DateTimeOffset(2026, 1, 1, 13, 0, 0, TimeSpan.Zero));

    private static MigrationConnection Connection(IBroadcastGrant grant) =>
        new(SourceKind.IBroadcast, [], IBroadcast: Account, IBroadcastGrant: grant);

    private static MigrationConnection WithGrant(MigrationConnection c, IBroadcastGrant grant) => c with { IBroadcastGrant = grant };

    [Fact]
    public void Replace_changes_the_kept_grant()
    {
        var store = new MemoryStore();
        var secrets = new MigrationSecrets(store);
        secrets.Keep(Name, Connection(First));

        secrets.Replace(Name, c => WithGrant(c, Second));

        var kept = secrets.Read(Name);
        Assert.NotNull(kept);
        Assert.Equal((Second, Account), (kept.IBroadcastGrant, kept.IBroadcast));
    }

    [Fact]
    public void Renewal_after_finish_does_not_keep_the_sign_in()
    {
        var store = new MemoryStore();
        var secrets = new MigrationSecrets(store);
        secrets.Keep(Name, Connection(First));

        secrets.Delete(Name);
        secrets.Replace(Name, c => WithGrant(c, Second));

        Assert.IsType<SecretRead.Absent>(store.Read(Name));
        Assert.Null(secrets.Read(Name));
    }

    [Fact]
    public void Replace_while_the_store_does_not_answer_writes_nothing()
    {
        var store = new MemoryStore { Answering = false };
        var secrets = new MigrationSecrets(store);

        secrets.Replace(Name, c => WithGrant(c, Second));

        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void A_delete_waits_for_a_replace_that_is_writing()
    {
        var store = new MemoryStore();
        var secrets = new MigrationSecrets(store);
        secrets.Keep(Name, Connection(First));
        var hold = new ManualResetEventSlim();
        store.HoldNextWrite = hold;

        var replace = Task.Run(() => secrets.Replace(Name, c => WithGrant(c, Second)));
        SpinWait.SpinUntil(() => store.Holding, TimeSpan.FromSeconds(5));
        var delete = Task.Run(() => secrets.Delete(Name));
        Thread.Sleep(50);
        hold.Set();
        Task.WaitAll(replace, delete);

        Assert.IsType<SecretRead.Absent>(store.Read(Name));
    }

    private sealed class MemoryStore : SecretStore
    {
        private readonly Dictionary<string, byte[]> _secrets = [];

        public bool Answering { get; init; } = true;
        public int Writes { get; private set; }
        public ManualResetEventSlim? HoldNextWrite { get; set; }
        public volatile bool Holding;

        public override string Name => "memory";

        public override SecretRead Read(string name)
        {
            if (!Answering)
                return new SecretRead.Failed("no answer");
            lock (_secrets)
                return _secrets.TryGetValue(name, out var bytes) ? new SecretRead.Found(bytes) : new SecretRead.Absent();
        }

        public override SecretWrite Write(string name, byte[] secret)
        {
            if (HoldNextWrite is { } hold)
            {
                HoldNextWrite = null;
                Holding = true;
                hold.Wait(TimeSpan.FromSeconds(5));
            }
            lock (_secrets)
            {
                Writes++;
                _secrets[name] = secret;
            }
            return new SecretWrite.Done();
        }

        public override SecretWrite Delete(string name)
        {
            lock (_secrets)
                _secrets.Remove(name);
            return new SecretWrite.Done();
        }
    }
}
