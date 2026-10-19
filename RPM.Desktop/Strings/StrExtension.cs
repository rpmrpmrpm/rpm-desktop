using Microsoft.UI.Xaml.Markup;

namespace RPM.Desktop.Strings;

/// <summary>
/// XAML markup extension resolving a <c>.resw</c> key against the active locale:
/// <c>Text="{loc:Str Key=Pick_Drives}"</c>. Use instead of <c>x:Uid</c> for
/// attached properties and strings inside DataTemplates, where the x:Uid syntax
/// is unreliable across Uno renderers. Resolves once, when the XAML loads. A change
/// of locale needs an app restart, as with x:Uid and PrimaryLanguageOverride.
/// </summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class StrExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    protected override object ProvideValue()
        => string.IsNullOrEmpty(Key) ? "" : Loc.Get(Key);
}
