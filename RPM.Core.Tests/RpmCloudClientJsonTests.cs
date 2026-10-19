using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace RPM.Core.Tests;

/// <summary>
/// The two API clients resolve a response's shape from their own JsonSerializerContext at the call,
/// rather than taking it as a parameter. This checks the list is complete.
/// </summary>
/// <remarks>
/// <para>Those clients answer three dozen routes between them, and passing the shape at each call
/// would mean naming the type twice on every line. The cost of looking it up instead is that a
/// response type nobody listed is only found when that route is first called. For a route used once
/// a month, a user would find it.</para>
///
/// <para>So it is found here instead. The contexts are private nested types and the methods are
/// private, so this reads the source rather than the assembly: it collects every
/// <c>SendAsync&lt;T&gt;</c> in each file and checks the same file declares a
/// <c>[JsonSerializable(typeof(T))]</c> for it. Crude, and it holds the one property that matters.</para>
/// </remarks>
public class RpmCloudClientJsonTests
{
    [Theory]
    [InlineData("Cloud/RpmCloudClient.cs")]
    [InlineData("Cloud/RpmCommunityClient.cs")]
    public void Every_answer_the_client_reads_is_a_shape_the_generator_was_given(string file)
    {
        var source = File.ReadAllText(Path.Combine(CoreDirectory(), file));

        var listed = Regex.Matches(source, @"\[JsonSerializable\(typeof\(([^)]+)\)\)\]")
            .Select(m => m.Groups[1].Value.Replace(" ", "", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var read = Regex.Matches(source, @"SendAsync<([A-Za-z0-9_<>?, ]+?)>\(")
            .Select(m => m.Groups[1].Value.Replace(" ", "", StringComparison.Ordinal))
            // object is the "no answer to read" case: those routes return before reading a body,
            // and T is never resolved. A nullable annotation says nothing about the shape.
            .Where(t => t is not ("object" or "object?" or "T" or "T?"))
            .Select(t => t.TrimEnd('?'))
            .ToHashSet(StringComparer.Ordinal);

        var missing = read.Where(t => !listed.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0,
            $"{file} reads these without listing them on its context, so the call throws at runtime: "
            + string.Join(", ", missing));
    }

    /// <summary>RPM.Core's folder, found from this file and not from the test host's working
    /// directory, which is a bin folder several levels down and moves with the framework.</summary>
    private static string CoreDirectory([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here)!)!, "RPM.Core");
}
