namespace RPM.Desktop.Tests;

/// <summary>
/// Tests that measure time run one at a time, with no other test class running beside them.
///
/// <para>xUnit runs test classes in parallel, on the same cores. A test that asserts how long something
/// takes, or how long one thread waits for another, then measures the other tests as much as the code. A
/// draw that costs 5 ms reads 17 ms, and a read that waits 50 ms behind a writer reads three seconds. The
/// limits in these tests are about the code, so they are measured where nothing else is running.</para>
///
/// <para>Anything that asserts on wall clock time belongs in here.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "timing";
}
