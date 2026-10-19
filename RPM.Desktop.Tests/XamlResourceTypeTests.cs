using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RPM.Desktop.Tests;

/// <summary>
/// No number resource is assigned to a property that takes a GridLength, a Thickness or a
/// CornerRadius. WinUI converts a string literal to those types, but not a resource: a
/// <c>{StaticResource}</c> that is an <c>x:Double</c> fails the page's load with a
/// <c>XamlParseException</c>. Uno converts it, so the problem does not show on macOS.
///
/// <para>Found on Windows in <c>LibraryProblemPage</c>, which gave a <c>RowDefinition.Height</c> the
/// double <c>WizardFooterHeight</c>. The page could not load, and the process ended with WER
/// P7 802b000a whenever a refusal or a missing folder showed that page.</para>
/// </summary>
public class XamlResourceTypeTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>The number types a XAML resource can be declared as.</summary>
    private static readonly HashSet<string> NumberTypes = ["Double", "Single", "Int32", "Int64"];

    /// <summary>Properties of any element whose type is a Thickness or a CornerRadius.</summary>
    private static readonly HashSet<string> StructProperties = ["Margin", "Padding", "BorderThickness", "CornerRadius"];

    /// <summary>The GridLength properties, by the element that has them.</summary>
    private static readonly Dictionary<string, string> GridLengthProperties = new()
    {
        ["RowDefinition"] = "Height",
        ["ColumnDefinition"] = "Width",
    };

    private static readonly Regex ResourceRef =
        new(@"^\{\s*(?:StaticResource|ThemeResource)\s+(?:ResourceKey\s*=\s*)?([\w.]+)\s*\}$", RegexOptions.Compiled);

    [Fact]
    public void No_number_resource_is_assigned_to_a_GridLength_Thickness_or_CornerRadius()
    {
        var root = RepoRoot();
        var files = XamlFiles(Path.Combine(root, "RPM.Desktop"))
            .Select(f => (File: f, Doc: XDocument.Load(f, LoadOptions.SetLineInfo)))
            .ToList();

        var numbers = files
            .SelectMany(f => f.Doc.Descendants().Where(e => e.Name.Namespace == X && NumberTypes.Contains(e.Name.LocalName)))
            .Select(e => (string?)e.Attribute(X + "Key"))
            .OfType<string>()
            .ToHashSet();

        // A scan that found no number resources would pass while checking nothing.
        Assert.Contains("WizardFooterHeight", numbers);

        var offenders = new List<string>();
        var checkedUses = 0;

        foreach (var (file, doc) in files)
        {
            foreach (var element in doc.Descendants())
            {
                foreach (var (property, value) in Assignments(element))
                {
                    if (!TakesAStruct(element, property) || ResourceRef.Match(value) is not { Success: true } match)
                        continue;

                    checkedUses++;
                    var key = match.Groups[1].Value;
                    if (!numbers.Contains(key))
                        continue;

                    var line = ((System.Xml.IXmlLineInfo)element).LineNumber;
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{line} {Owner(element)}.{property} = {key}");
                }
            }
        }

        // The bar is low on purpose: only a few such uses exist. Zero would mean the scan has stopped
        // matching.
        Assert.True(checkedUses >= 3, $"only {checkedUses} resource uses on these properties found; the scan is wrong");

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} number resources are assigned to a GridLength, Thickness or CornerRadius:{Environment.NewLine}"
            + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>The property assignments an element makes: its attributes, or for a Setter its one
    /// Property and Value.</summary>
    private static IEnumerable<(string Property, string Value)> Assignments(XElement element)
    {
        if (element.Name.LocalName == "Setter")
        {
            if ((string?)element.Attribute("Property") is { } property && (string?)element.Attribute("Value") is { } value)
                yield return (property, value);
            yield break;
        }

        foreach (var attribute in element.Attributes().Where(a => a.Name.Namespace == XNamespace.None))
            yield return (attribute.Name.LocalName, attribute.Value);
    }

    private static bool TakesAStruct(XElement element, string property) =>
        StructProperties.Contains(property)
        || (GridLengthProperties.TryGetValue(Owner(element), out var gridLength) && gridLength == property);

    /// <summary>The element a property is set on: the Setter's style target, or the element
    /// itself.</summary>
    private static string Owner(XElement element)
    {
        if (element.Name.LocalName != "Setter")
            return element.Name.LocalName;

        var target = element.Ancestors().FirstOrDefault(a => a.Name.LocalName == "Style")?.Attribute("TargetType")?.Value ?? "";
        return target.Contains(':') ? target[(target.IndexOf(':') + 1)..] : target;
    }

    private static IEnumerable<string> XamlFiles(string app)
        => Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>Walks up from the test binaries to the folder holding RPM.sln.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RPM.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
