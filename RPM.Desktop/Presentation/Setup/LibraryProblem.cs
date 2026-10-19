using RPM.Core.Library;

namespace RPM.Desktop.Presentation.Setup;

/// <summary>
/// Where the problem screen is shown. This decides what its two buttons do.
/// </summary>
public enum ProblemPlace
{
    /// <summary>In place of the library at launch, in the shell's frame. No collection is open, so
    /// nothing is subscribed to a mirror. Try again repeats the launch. Choose another folder opens
    /// the setup wizard's folder step.</summary>
    Landing,

    /// <summary>Over a running library, in the shell's overlay, after a read of the open collection
    /// failed. The library page stays alive under it and is not rebuilt during one run. Try again
    /// and Choose another folder both open a collection in the same way as the Settings folder
    /// change. The shell hides the screen once the open collection has no trouble.</summary>
    OverLibrary,
}

/// <summary>
/// What the problem screen is navigated with: the failure, and where it is shown. Both are required,
/// so the screen cannot be put up without saying which buttons it gets.
/// </summary>
public sealed record LibraryProblem(LibraryOpening.Trouble Trouble, ProblemPlace Place);
