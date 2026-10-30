namespace RPM.Desktop.Strings;

/// <summary>
/// Static gateway to the active locale's <c>Resources.resw</c>. Use from
/// code-behind / view-models where <c>x:Uid</c> / <c>loc:Str</c> can't reach
/// (dynamic content, generated subtitles). Loader is cached so lookups stay cheap.
/// </summary>
/// <remarks>
/// <b>A chosen language is read through a context of its own.</b> The loader answers in Windows' language
/// and keeps that answer for the life of the process. <see cref="Use"/> gives the WinUI head a resource
/// context whose language is the one the user chose, so every string asked for after it is in that
/// language. Every string in the app comes through here (<c>loc:Str</c> included), so a screen built after
/// the change is in the new language. One built before it keeps the old one until it is built again.
/// </remarks>
internal static class Loc
{
    private static readonly Microsoft.Windows.ApplicationModel.Resources.ResourceLoader _loader = new();

#if RPM_WINUI
    /// <summary>The table and the context to read it in, once <see cref="Use"/> has been called. Swapped
    /// together as one object, so a reader on another thread never sees a map with the wrong context.</summary>
    private static (Microsoft.Windows.ApplicationModel.Resources.ResourceMap Map,
                    Microsoft.Windows.ApplicationModel.Resources.ResourceContext Context)? _chosen;
#endif

    /// <summary>Reads every later string in <paramref name="tag"/>, or in Windows' own language when it is
    /// null. See <see cref="Services.Shell.AppLanguage"/>, which is the only caller.</summary>
    public static void Use(string? tag)
    {
#if RPM_WINUI
        try
        {
            var manager = new Microsoft.Windows.ApplicationModel.Resources.ResourceManager();
            var context = manager.CreateResourceContext();
            if (!string.IsNullOrEmpty(tag))
                context.QualifierValues["Language"] = tag;
            _chosen = (manager.MainResourceMap.GetSubtree("Resources"), context);
        }
        catch
        {
            _chosen = null;   // the loader still answers, in Windows' language
        }
#endif
    }

    /// <summary>
    /// The string for <paramref name="key"/>, or the key itself when the table has no such row.
    /// WinUI's loader throws a COMException ("NamedResource Not Found") for a missing key, and some
    /// call sites run on a task nobody awaits, where that throw reaches the finalizer and takes the
    /// process down. A key rendered as its own name on screen is the intended failure: visible, and
    /// survivable.
    /// </summary>
    public static string Get(string key)
    {
        try
        {
#if RPM_WINUI
            if (_chosen is var (map, context))
                return map.TryGetValue(key, context)?.ValueAsString ?? key;
#endif
            return _loader.GetString(key);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return key;
        }
    }

    /// <summary>
    /// The string for <paramref name="key"/> in the language <paramref name="tag"/>, whatever the app is
    /// speaking: the welcome's greeting in every language. Where the head cannot read another language
    /// (the Skia heads), the string in the language on screen.
    /// </summary>
    public static string GetIn(string key, string tag)
    {
#if RPM_WINUI
        try
        {
            if (!_others.TryGetValue(tag, out var other))
            {
                var manager = new Microsoft.Windows.ApplicationModel.Resources.ResourceManager();
                var context = manager.CreateResourceContext();
                context.QualifierValues["Language"] = tag;
                other = (manager.MainResourceMap.GetSubtree("Resources"), context);
                _others[tag] = other;
            }
            return other.Map.TryGetValue(key, other.Context)?.ValueAsString ?? Get(key);
        }
        catch
        {
            return Get(key);
        }
#else
        return Get(key);
#endif
    }

#if RPM_WINUI
    private static readonly Dictionary<string, (Microsoft.Windows.ApplicationModel.Resources.ResourceMap Map,
        Microsoft.Windows.ApplicationModel.Resources.ResourceContext Context)> _others = [];
#endif

    public static string Format(string key, params object?[] args)
        => string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(key), args);
}
