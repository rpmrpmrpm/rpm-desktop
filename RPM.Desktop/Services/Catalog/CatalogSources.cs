using RPM.Core.Metadata.Providers;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Services.Catalog;

/// <summary>
/// The words a lookup shows on its start button, saying what it will search.
///
/// <para>The rpm database is searched whenever there is a session to reach it with. Discogs is added once
/// the user has connected an account. The list is read when it is shown, from the providers that can answer
/// right now, so the text never promises a search that will not happen.</para>
///
/// <para>The app's own provider is called "RPM" in the progress list of the lookup window, where it is a
/// column heading. In a sentence it needs an article and a noun, so its name comes from the string table.
/// The upstream catalogue behind the server is never named in the UI.</para>
/// </summary>
public static class CatalogSources
{
    /// <summary>"the rpm database", or "the rpm database and Discogs".</summary>
    public static string Describe(CatalogService catalog)
    {
        var names = catalog.Search.Available
            .Select(p => p.Id == RpmMetadataProvider.ProviderId ? Loc.Get("Lookup_SourceRpm") : p.DisplayName)
            .ToList();

        return names.Count switch
        {
            // Nothing can answer when the user is signed out. The name is still given: this text is the
            // tooltip of the button that asks for sign-in, so it says what the button is for.
            0 => Loc.Get("Lookup_SourceRpm"),
            1 => names[0],
            _ => string.Join(Loc.Get("Common_ListSeparator"), names[..^1])
                 + $" {Loc.Get("Common_And")} " + names[^1],
        };
    }
}
