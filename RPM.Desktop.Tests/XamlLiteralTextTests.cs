using System.Xml.Linq;

namespace RPM.Desktop.Tests;

/// <summary>
/// No XAML that ships puts English on screen. Literal English text is not allowed in Release screens. Each
/// piece of text must come from a localisation key.
///
/// <para>A property that shows text (<c>Text</c>, <c>Content</c>, a tooltip, a placeholder, an
/// accessible name and the others in <see cref="TextProperties"/>) takes a <c>{loc:Str Key=…}</c>, a
/// binding, or text with no letter in it (a glyph). Text between tags counts too, and so does a
/// <c>Setter</c> that sets one of those properties.</para>
///
/// <para>What is left as written is listed in <see cref="AsWritten"/>, each with the reason, and the
/// Debug-only screens are not read. A placeholder that code replaces is not an exception: it shows
/// for a frame, in English, before the code runs.</para>
/// </summary>
public class XamlLiteralTextTests
{
    private static readonly HashSet<string> TextProperties =
    [
        "Text", "Content", "Header", "PlaceholderText", "Title", "Label", "Description", "Message",
        "Subtitle", "PrimaryButtonText", "SecondaryButtonText", "CloseButtonText", "OnContent",
        "OffContent", "ToolTipService.ToolTip", "AutomationProperties.Name",
        "AutomationProperties.HelpText",
    ];

    /// <summary>Elements whose text between the tags is shown.</summary>
    private static readonly HashSet<string> TextHolders =
    [
        "TextBlock", "Run", "Span", "Bold", "Italic", "Underline", "Hyperlink", "ToolTip", "String",
        "ComboBoxItem", "ListViewItem", "Button", "HyperlinkButton", "CheckBox", "RadioButton",
    ];

    /// <summary>Text that reads the same in every language, and why.</summary>
    private static readonly Dictionary<string, string> AsWritten = new(StringComparer.Ordinal)
    {
        ["rpm"] = "the app's name",
        ["RPaMp"] = "the player skin's name, drawn as a wordmark",
        ["Discogs"] = "the catalogue's name, on its own mark",
        ["fanart.tv"] = "the artwork site's name",
        ["E"] = "the explicit mark, which streaming services show as E in every language",
        ["AD"] = "the mark beside a radio station that carries advertising, the explicit mark's twin",
        ["kbps"] = "a unit symbol, after a bitrate in the RPaMp readout",
        ["kHz"] = "a unit symbol, after a sample rate in the RPaMp readout",
    };

    /// <summary>Elements whose whole subtree is left as written. Each is collapsed in a Release build
    /// and shown only by Debug code.</summary>
    private static readonly HashSet<string> DebugOnly =
    [
        "SupporterDebugCard",   // CloudSettingsPage: the supporter pretend switch, made visible under #if DEBUG
    ];

    /// <summary>Folders whose screens are compiled into Debug builds only.</summary>
    private static readonly string[] DebugFolders = ["Gallery", "Dev", "MixLab"];

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void No_shipping_xaml_shows_a_literal()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        var read = 0;

        foreach (var file in XamlFiles(Path.Combine(root, "RPM.Desktop")))
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var (line, what, value) in Literals(doc.Root!))
                offenders.Add($"{Path.GetRelativePath(root, file)}:{line} {what}=\"{value}\"");
            read += doc.Descendants().Count();
        }

        // A scan that quietly read nothing would pass while saying nothing about the app.
        Assert.True(read > 10_000, $"only {read} elements read; the scan is wrong");

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} literals in shipping XAML. Add the words to scripts/strings.json and use "
            + "{loc:Str Key=…}, or, for a name that reads the same in every language, add it to AsWritten:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>The rule itself, on small cases, so a change to it is seen here first.</summary>
    [Theory]
    [InlineData("""<TextBlock Text="Search:"/>""", 1)]
    [InlineData("""<Button Content="CLEAR"/>""", 1)]
    [InlineData("""<Grid ToolTipService.ToolTip="Mute"/>""", 1)]
    [InlineData("""<ComboBox PlaceholderText="Presets"/>""", 1)]
    [InlineData("""<TextBlock>Playlist</TextBlock>""", 1)]
    [InlineData("""<TextBlock><Run Text="{Binding Count}"/><Run Text=" tracks"/></TextBlock>""", 1)]
    [InlineData("""<TextBlock><Run Text="{Binding Kbps}"/><Run Text=" kbps"/></TextBlock>""", 0)]
    [InlineData("""<Setter Property="Text" Value="stereo"/>""", 1)]
    [InlineData("""<TextBlock Text="{loc:Str Key=Amp_Search}"/>""", 0)]
    [InlineData("""<TextBlock Text="{Binding Title}"/>""", 0)]
    [InlineData("""<TextBlock Text="&#xf04b;"/>""", 0)]
    [InlineData("""<TextBlock Text="✕"/>""", 0)]
    [InlineData("""<TextBlock Text="0:00"/>""", 0)]
    [InlineData("""<TextBlock Text="rpm"/>""", 0)]
    [InlineData("""<Setter Property="Width" Value="Auto"/>""", 0)]
    [InlineData("""<Border x:Name="SupporterDebugCard"><TextBlock Text="Debug: pretend"/></Border>""", 0)]
    [InlineData("""<TextBlock x:Name="Label" Tag="Header"/>""", 0)]
    public void A_literal_is_text_with_a_letter_in_a_property_that_shows_it(string xaml, int expected)
    {
        var wrapped = $"""<Root xmlns="urn:x" xmlns:x="{X.NamespaceName}" xmlns:loc="urn:loc">{xaml}</Root>""";

        Assert.Equal(expected, Literals(XDocument.Parse(wrapped, LoadOptions.SetLineInfo).Root!).Count());
    }

    private static IEnumerable<(int Line, string What, string Value)> Literals(XElement element)
    {
        if ((string?)element.Attribute(X + "Name") is { } name && DebugOnly.Contains(name))
            yield break;

        var line = ((System.Xml.IXmlLineInfo)element).LineNumber;

        foreach (var attribute in element.Attributes())
        {
            var property = attribute.Name.NamespaceName.Length == 0 ? attribute.Name.LocalName : null;
            if (property is not null && TextProperties.Contains(property) && IsLiteral(attribute.Value))
                yield return (line, $"{element.Name.LocalName} {property}", attribute.Value);
        }

        if (element.Name.LocalName == "Setter"
            && (string?)element.Attribute("Property") is { } set && TextProperties.Contains(set)
            && (string?)element.Attribute("Value") is { } value && IsLiteral(value))
            yield return (line, $"Setter {set}", value);

        if (TextHolders.Contains(element.Name.LocalName))
        {
            foreach (var text in element.Nodes().OfType<XText>())
                if (IsLiteral(text.Value.Trim()))
                    yield return (line, element.Name.LocalName, text.Value.Trim());
        }

        foreach (var child in element.Elements())
            foreach (var found in Literals(child))
                yield return found;
    }

    private static bool IsLiteral(string value)
        => value.Length > 0
           && !value.StartsWith('{')
           && value.Any(char.IsLetter)
           && !AsWritten.ContainsKey(value.Trim());

    private static IEnumerable<string> XamlFiles(string app)
    {
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{sep}obj{sep}", StringComparison.Ordinal)
                     && !p.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
                     && !DebugFolders.Any(f => p.Contains($"{sep}Presentation{sep}{f}{sep}", StringComparison.Ordinal)));
    }

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
