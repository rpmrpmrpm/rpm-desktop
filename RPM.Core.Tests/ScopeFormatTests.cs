using RPM.Core.Diagnostics;

namespace RPM.Core.Tests;

/// <summary>
/// Writes a ring for the Python reader to read.
///
/// <para>The format is agreed between two languages and nothing in either compiler checks the
/// agreement. So it is checked here instead: this writes a fixture with every field set to a number
/// that could not be arrived at by accident, and <c>tools/rpmscope/tests/test_ring.py</c> asserts the
/// same numbers come back out. A change to the layout that forgets the Python side fails there,
/// loudly, and does not show up months later as a trace that reads wrong.</para>
/// </summary>
[Collection(ScopeCollection.Name)]
public class ScopeFormatTests
{
    /// <summary>A fixed path, because the Python test has to be able to find it without being
    /// told.</summary>
    public static string FixtureDir => Path.Combine(Path.GetTempPath(), "rpmscope-fixture");

    [Fact]
    public void WriteTheFixtureThePythonReaderReads()
    {
        if (Directory.Exists(FixtureDir))
            Directory.Delete(FixtureDir, recursive: true);
        Directory.CreateDirectory(FixtureDir);

        using (var names = ScopeNames.Open(Path.Combine(FixtureDir, "scope.names")))
        using (var ring = ScopeRing.Create(Path.Combine(FixtureDir, "scope.ring"), capacity: 64))
        {
            ring.Write(new ScopeRecord
            {
                Nanos = 1_000_000_000,
                Kind = (ushort)ScopeKind.GateHold,
                Thread = 7,
                JobId = 111,
                ParentId = 222,
                A = 19_371_000_000,
                B = 4_000_000,
                NameId = names.Id("GetAlbumCloudRollup"),
                Detail = names.Id("read"),
                Flags = 0x0201,
            });

            ring.Write(new ScopeRecord
            {
                Nanos = 2_000_000_000,
                Kind = (ushort)ScopeKind.FrameOverrun,
                Thread = 1,
                A = 1_516_000_000,
            });
        }

        Assert.True(File.Exists(Path.Combine(FixtureDir, "scope.ring")));
        Assert.True(File.Exists(Path.Combine(FixtureDir, "scope.names")));
    }
}
