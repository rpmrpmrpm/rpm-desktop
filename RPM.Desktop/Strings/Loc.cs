namespace RPM.Desktop.Strings;

/// <summary>
/// Static gateway to the active locale's <c>Resources.resw</c>. Use from
/// code-behind / view-models where <c>x:Uid</c> / <c>loc:Str</c> can't reach
/// (dynamic content, generated subtitles). Loader is cached so lookups stay cheap.
/// </summary>
internal static class Loc
{
    private static readonly Microsoft.Windows.ApplicationModel.Resources.ResourceLoader _loader = new();

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
            return _loader.GetString(key);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return key;
        }
    }

    public static string Format(string key, params object?[] args)
        => string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(key), args);
}
