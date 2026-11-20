namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>
/// Which record's sleeve the track pane's cover square holds, as a settled answer.
///
/// <para>The next track keeps the sleeve that is up only when this says it is that record's own. It is
/// written where the square is written, and nowhere else. A read that is started for a record and then
/// replaced by the next track never reaches the square, so it says nothing about what the square
/// holds. If the folder were noted when the read starts, a quick second track on a cloud-only album
/// would keep the previous album's cover, because the read that was going to replace it was
/// dropped.</para>
/// </summary>
internal sealed class PaneSleeve
{
    /// <summary>The album folder of the settled sleeve in the square, or null when the square holds no
    /// settled answer.</summary>
    private string? _folder;

    /// <summary>Whether the square holds the settled sleeve of the record filed in
    /// <paramref name="folder"/>. Never true for "", which is a file outside any collection.</summary>
    public bool Holds(string folder)
        => folder.Length > 0 && string.Equals(folder, _folder, StringComparison.Ordinal);

    /// <summary>The square was just written for the record filed in <paramref name="folder"/>.</summary>
    /// <param name="settled">False when the sleeve could not be read. The square then shows the
    /// placeholder, which is not an answer, and the next track of the record asks again.</param>
    public void Painted(string folder, bool settled) => _folder = settled ? folder : null;

    /// <summary>The pane was emptied. The next record asks for its sleeve, even if it is this one.</summary>
    public void Forget() => _folder = null;
}
