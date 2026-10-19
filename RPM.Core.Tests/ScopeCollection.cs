namespace RPM.Core.Tests;

/// <summary>
/// Everything that touches the instrument runs one at a time.
///
/// <para><c>Scope</c> is deliberately process-global: there is one ring per process, and everything
/// writes into it without being handed one. xUnit runs test classes in parallel. So a class that
/// merely TAKES a gate would write into whichever ring another class happens to have opened, and
/// the test that counts records would find somebody else's. That is not a random failure to retry.
/// It is the same sharing the instrument is for, arriving where it is not wanted.</para>
///
/// <para>Anything that starts a ring, or that produces notes a ring would catch, belongs in here.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ScopeCollection
{
    public const string Name = "scope";
}
