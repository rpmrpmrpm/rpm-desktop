namespace RPM.Desktop.Presentation.Import;

/// <summary>What one press of the Import button means at this moment.</summary>
public enum ImportPress
{
    /// <summary>Nothing to file, or an import already running: the press does nothing.</summary>
    Nothing,

    /// <summary>File the queue.</summary>
    Import,

    /// <summary>Ask about the tag editor's unapplied edits first, then file the queue or not.</summary>
    AskAboutTags,
}

/// <summary>How the unsaved-tags question was answered.</summary>
public enum UnsavedTags
{
    /// <summary>Write the edits to the rows, then import.</summary>
    Apply,

    /// <summary>Throw the edits away, then import.</summary>
    Discard,

    /// <summary>The user did not want to import.</summary>
    Cancel,
}

/// <summary>
/// The rule the Import button follows: an import never runs past an edit that is still in the tag
/// editor.
///
/// <para>This is kept out of the click handler because it is the part that must be right. The panel
/// writes to copies of the rows and saves them on Apply. An import that ran while the panel still had
/// unapplied edits would file the old values, and the edit would be lost. Both cannot be allowed, so
/// the press asks rather than guesses. The decision depends only on three booleans, so it can be
/// tested without a window.</para>
/// </summary>
public static class ImportGate
{
    /// <summary>
    /// What the press means.
    /// </summary>
    /// <param name="canImport">Whether the model would file anything at all. See
    /// <see cref="Main.Models.MainModel.CanImport"/>.</param>
    /// <param name="panelOnScreen">Whether the tag editor is actually showing. Edits in a panel that is
    /// stepped aside cannot be seen, so asking about them would ask the user about something they cannot
    /// see.</param>
    /// <param name="panelHasEdits">Whether the panel's Apply would write anything. See
    /// <see cref="ImportTagPanel.HasPendingEdits"/>.</param>
    public static ImportPress For(bool canImport, bool panelOnScreen, bool panelHasEdits)
        => !canImport ? ImportPress.Nothing
            : panelOnScreen && panelHasEdits ? ImportPress.AskAboutTags
            : ImportPress.Import;
}
