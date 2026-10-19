namespace RPM.Desktop.Strings;

/// <summary>
/// A value and the string key that puts it into words: <c>Track_BitDepth</c> and "24" read "24-bit"
/// in English and "24 bits" in French. A null key means the value reads the same in every language
/// ("5.1", "7.1").
///
/// <para>Kept apart from the text so a test can check what a cell or a status line says without the
/// string table, which only the running app can read: <see cref="Loc"/> needs the app package and
/// throws in the test host. A key handed out here is not written at a <c>Loc.Get</c> call, so the
/// repo-wide key test cannot see it; the code that hands it out has its own test against
/// <c>scripts/strings.json</c>.</para>
/// </summary>
public readonly record struct Phrase(string? StringKey, string Value)
{
    /// <summary>A value with no words around it.</summary>
    public static Phrase AsIs(string value) => new(null, value);

    /// <summary>What the screen shows.</summary>
    public string Text => StringKey is null ? Value : Loc.Format(StringKey, Value);
}
