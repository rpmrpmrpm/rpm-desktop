using RPM.Core.Nas;
using RPM.Core.Sharing;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Services.Nas;

/// <summary>
/// The space check before an import into a library on a NAS. The daemon cannot stop a write that comes
/// over SMB, so the import asks the NAS first and refuses before anything is copied.
/// </summary>
public static class NasImportSpace
{
    /// <summary>
    /// The answer for an import of <paramref name="incomingBytes"/>, or null when the NAS's space is not
    /// known. An unknown space does not block an import: SMB itself refuses a write to a full disk.
    /// </summary>
    public static NasSpaceAnswer? Decide(NasHello? space, long incomingBytes) =>
        space is null ? null : NasSpace.Check(space, incomingBytes);

    /// <summary>
    /// Asks the NAS that serves the open library. Returns the line to show and whether the import must
    /// stop. Both are null and false when the library is not on a NAS or its space is not known.
    /// </summary>
    public static async Task<(string? Line, bool Refuse)> CheckAsync(
        NasLocation? location, string libraryRoot, NasDiscovery discovery, long incomingBytes)
    {
        if (location is null || NasLibrary.IdOf(libraryRoot) is not { } id || discovery.LibraryServing(id) is not { } nas)
            return (null, false);
        var hello = await NasLibraryService.HelloAsync(nas, CancellationToken.None);
        var space = hello?.Nas;
        var limit = space is null ? 0 : space.LimitBytes ?? space.UsedBytes + space.FreeBytes;
        return Decide(space, incomingBytes) switch
        {
            NasSpaceAnswer.Full => (Loc.Format("Nas_SpaceFull", location.Name, ByteSize.Human(space!.UsedBytes), ByteSize.Human(limit)), true),
            NasSpaceAnswer.AlmostFull => (Loc.Format("Nas_SpaceAlmostFull", ByteSize.Human(space!.UsedBytes + incomingBytes), ByteSize.Human(limit)), false),
            _ => (null, false),
        };
    }
}
