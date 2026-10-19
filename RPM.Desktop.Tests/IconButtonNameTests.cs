using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RPM.Desktop.Tests;

/// <summary>
/// Every button that shows only an icon must have an accessible name. A screen reader announces such a
/// button as "button" if it has no name. A tooltip is not a name, because neither the macOS view nor the
/// Windows automation peer is required to read one.
///
/// <para><b>What counts as a name:</b> <c>AutomationProperties.Name</c> or <c>LabeledBy</c> in the XAML. A
/// <c>SetName</c> call on the button's <c>x:Name</c> in the code beside it also counts. So does
/// <c>AccessibilityView="Raw"</c>, which takes the button out of the accessibility tree on purpose. That is
/// used for a copy of a control that is already named elsewhere.</para>
///
/// <para><b>What counts as icon-only</b> is decided from the content. The button has no text, and it has
/// either an icon element, a glyph (text with no letter or digit), or text in a Font Awesome face. A button
/// whose content is empty in the XAML, and is not in an icon face, gets its content in code. The test
/// cannot see that, so it does not judge the button.</para>
/// </summary>
public class IconButtonNameTests
{
    private static readonly HashSet<string> ButtonTypes =
    [
        "Button", "ToggleButton", "RepeatButton", "HyperlinkButton", "DropDownButton", "SplitButton",
        "ToggleSplitButton", "AppBarButton", "AppBarToggleButton",
        "RpmButton", "RpmPrimaryButton", "RpmDangerButton", "RpmLink",
    ];

    /// <summary>Layout only. These elements hold content, but they are not content themselves.</summary>
    private static readonly HashSet<string> Containers =
    [
        "Grid", "StackPanel", "Border", "Viewbox", "Canvas", "RelativePanel", "ContentPresenter",
        "ContentControl",
    ];

    /// <summary>Each Font Awesome face is a resource whose key has <c>Fa</c> as a separate word:
    /// <c>Fa</c>, <c>FaRegular</c>, <c>PegasusFa</c>, <c>PegasusPlayerFa</c>, <c>FaBrandFont</c>.</summary>
    private static readonly Regex IconFace = new(@"Resource\s+(?:\w*[a-z])?Fa(?:[A-Z]\w*)?\s*\}", RegexOptions.Compiled);

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Every_icon_only_button_has_an_accessible_name()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        var iconOnly = 0;

        foreach (var file in XamlFiles(Path.Combine(root, "RPM.Desktop")))
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            var code = CodeBeside(file);

            foreach (var button in doc.Descendants().Where(e => ButtonTypes.Contains(e.Name.LocalName)))
            {
                if (!IsIconOnly(button))
                    continue;

                iconOnly++;
                if (HasName(button, code))
                    continue;

                var line = ((System.Xml.IXmlLineInfo)button).LineNumber;
                var name = (string?)button.Attribute(X + "Name");
                var tip = (string?)button.Attributes().FirstOrDefault(a => a.Name.LocalName == "ToolTipService.ToolTip");
                offenders.Add($"{Path.GetRelativePath(root, file)}:{line} {button.Name.LocalName}"
                    + (name is null ? "" : $" {name}") + (tip is null ? "" : $" (tooltip {tip})"));
            }
        }

        // A scan that matches nothing would pass without checking the app. This line guards against that.
        Assert.True(iconOnly > 150, $"only {iconOnly} icon-only buttons found; the scan is wrong");

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} icon-only buttons have no accessible name:{Environment.NewLine}"
            + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>If code changes a tooltip with state, it must go through <c>IconName.Set</c>, which changes
    /// the name too. <c>ToolTipService.SetToolTip</c> alone would leave the name with the old state. For
    /// example, a pin that is already on would still read out "Keep the controls visible".</summary>
    [Fact]
    public void Code_changes_an_icon_buttons_tooltip_and_name_together()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var file in XamlFiles(Path.Combine(root, "RPM.Desktop")))
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            var code = CodeBeside(file);

            foreach (var button in doc.Descendants().Where(e => ButtonTypes.Contains(e.Name.LocalName)))
            {
                var name = (string?)button.Attribute(X + "Name");
                if (name is null || !IsIconOnly(button))
                    continue;

                if (Regex.IsMatch(code, $@"\bToolTipService\.SetToolTip\(\s*{Regex.Escape(name)}\s*,"))
                    offenders.Add($"{Path.GetRelativePath(root, file)}: {name}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Code sets the tooltip of these icon-only buttons without their name; use IconName.Set:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>The rule itself, on small cases, so a change to it is seen here first.</summary>
    [Theory]
    [InlineData("""<Button Content="&#xf04b;" FontFamily="{StaticResource Fa}"/>""", true)]
    [InlineData("""<Button Content="✕"/>""", true)]
    [InlineData("""<Button><FontIcon Glyph="&#xE711;"/></Button>""", true)]
    [InlineData("""<Button><TextBlock FontFamily="{StaticResource PegasusFa}"/></Button>""", true)]
    [InlineData("""<Button FontFamily="{StaticResource FaRegular}"/>""", true)]
    [InlineData("""<Button><Grid><Path Data="M0,0"/></Grid></Button>""", true)]
    [InlineData("""<Button Content="{Binding PlayGlyph}"/>""", true)]
    [InlineData("""<Button Content="Play"/>""", false)]
    [InlineData("""<Button Content="{loc:Str Key=Play}"/>""", false)]
    [InlineData("""<Button><StackPanel><FontIcon Glyph="&#xE711;"/><TextBlock Text="{loc:Str Key=Close}"/></StackPanel></Button>""", false)]
    [InlineData("""<Button><TextBlock x:Name="Label"/></Button>""", false)]
    [InlineData("""<Button x:Name="FilledInCode"/>""", false)]
    [InlineData("""<Button Content="A"><Button.Flyout><Flyout><FontIcon/></Flyout></Button.Flyout></Button>""", false)]
    [InlineData("""<Button Content="{Binding Title}"/>""", false)]
    [InlineData("""<Button FontFamily="{StaticResource RpmDisplayFont}"/>""", false)]
    public void Icon_only_is_read_off_the_content(string xaml, bool iconOnly)
    {
        var wrapped = $"""<Root xmlns="urn:x" xmlns:x="{X.NamespaceName}" xmlns:loc="urn:loc">{xaml}</Root>""";
        var button = XDocument.Parse(wrapped).Root!.Elements().Single();

        Assert.Equal(iconOnly, IsIconOnly(button));
    }

    private static bool HasName(XElement button, string code)
    {
        foreach (var a in button.Attributes())
        {
            switch (a.Name.LocalName)
            {
                case "AutomationProperties.Name" or "AutomationProperties.LabeledBy":
                    return true;
                case "AutomationProperties.AccessibilityView" when a.Value == "Raw":
                    return true;
            }
        }

        if (button.Elements().Any(e => e.Name.LocalName == "AutomationProperties.Name"))
            return true;

        var name = (string?)button.Attribute(X + "Name");
        return name is not null
            && Regex.IsMatch(code, $@"\b(?:AutomationProperties\.SetName|IconName\.Set)\(\s*{Regex.Escape(name)}\s*,");
    }

    private static bool IsIconOnly(XElement button)
    {
        var face = IsIconFace((string?)button.Attribute("FontFamily"));
        var seen = new Seen();

        Classify((string?)button.Attribute("Content"), face, seen);
        Classify((string?)button.Attribute("Label"), face: false, seen);
        foreach (var child in ContentOf(button))
            Visit(child, face, seen);

        if (seen.Text)
            return false;

        return seen.Icon || (seen.Empty && face);
    }

    private static void Visit(XElement node, bool face, Seen seen)
    {
        seen.Empty = false;
        var local = node.Name.LocalName;
        if (node.Attribute("FontFamily") is { } family)
            face = IsIconFace(family.Value);

        if (local is "TextBlock" or "Run")
        {
            var text = (string?)node.Attribute("Text");
            var runs = node.Elements().Where(e => e.Name.LocalName is "Run" or "Span" or "Bold" or "Italic").ToList();
            if (text is null && runs.Count > 0)
            {
                foreach (var run in runs)
                    Visit(run, face, seen);
                return;
            }

            text ??= string.IsNullOrWhiteSpace(node.Value) ? null : node.Value;

            // No text in the XAML means the text is set in code. It is a glyph if the face is an icon face,
            // and a word otherwise.
            if (text is null)
            {
                if (face) seen.Icon = true;
                else seen.Text = true;
                return;
            }

            Classify(text, face, seen);
            return;
        }

        if (Containers.Contains(local))
        {
            Classify((string?)node.Attribute("Content"), face, seen);
            foreach (var child in ContentOf(node))
                Visit(child, face, seen);
            return;
        }

        // Anything else that draws is a picture, such as FontIcon, PathIcon, Image, Path, a shape, or one
        // of the app's own drawn controls (ExplicitBadge, DiscogsIcon, PlayingBars).
        seen.Icon = true;
    }

    private static void Classify(string? value, bool face, Seen seen)
    {
        if (string.IsNullOrEmpty(value))
            return;

        seen.Empty = false;

        if (value.StartsWith('{'))
        {
            if (value.StartsWith("{loc:Str", StringComparison.Ordinal))
                seen.Text = true;
            else if (face || Regex.IsMatch(value, "Glyph|Icon"))
                seen.Icon = true;
            else
                seen.Text = true;
            return;
        }

        if (value.Any(char.IsLetterOrDigit))
            seen.Text = true;
        else
            seen.Icon = true;
    }

    /// <summary>Child elements that are content, not property elements such as
    /// <c>Button.Flyout</c>.</summary>
    private static IEnumerable<XElement> ContentOf(XElement element)
        => element.Elements().Where(e => !e.Name.LocalName.Contains('.'));

    private static bool IsIconFace(string? family)
        => family is not null && IconFace.IsMatch(family);

    /// <summary>The code of the view itself: the partial class in <c>PegasusChrome.xaml.cs</c> and every
    /// <c>PegasusChrome.*.cs</c> beside it. Not the whole folder, because another view there can have a
    /// button of the same name.</summary>
    private static string CodeBeside(string xamlFile)
    {
        var dir = Path.GetDirectoryName(xamlFile)!;
        var view = Path.GetFileNameWithoutExtension(xamlFile);
        return string.Concat(Directory.EnumerateFiles(dir, view + ".*.cs").Select(File.ReadAllText));
    }

    private static IEnumerable<string> XamlFiles(string app)
        => Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private sealed class Seen
    {
        public bool Text;
        public bool Icon;
        public bool Empty = true;
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
