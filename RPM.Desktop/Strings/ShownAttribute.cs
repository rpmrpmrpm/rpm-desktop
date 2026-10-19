namespace RPM.Desktop.Strings;

/// <summary>
/// Marks a parameter or property whose string goes on screen. Build rule RPM016 reports a literal with
/// words in it handed to one, as it does for a XAML element's <c>Text</c> or a tooltip: the words
/// belong in <c>scripts/strings.json</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
internal sealed class ShownAttribute : Attribute;
