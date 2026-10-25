using RPM.Core.Cloud;
using RPM.Desktop.Services.Account;

namespace RPM.Desktop.Services.Sharing;

/// <summary>
/// The songs the owner of a shared collection has made favourites, for the home screen to recommend from
/// while that collection is open (see <see cref="RPM.Core.Library.BorrowedHome.Quick"/>).
///
/// <para><b>From the cloud, because nothing else carries them.</b> The listing a computer on this network
/// gives a guest leaves a person's favourites out on purpose (see PeerCatalogBuilder), and so does the
/// cloud's album marks route for anybody but the owner. The cloud listing of a shared collection
/// (<c>cloud/list?owner=</c>) does carry each song's favourite, behind the same share check as its music,
/// and it is what the TV and the phone draw a shared collection from. So the favourites are the ones on
/// the owner's songs that are in the cloud. Nothing here shows them: no heart is drawn on a borrowed
/// shelf. They only decide what the quick selection puts forward.</para>
///
/// <para><b>Read once per owner for the life of the app.</b> The listing is the whole collection, about
/// twenty round trips for a large one, so it is read in the background the first time home asks for an
/// owner, and only the ids of the favourites are kept. <see cref="Landed"/> says when they are in. A read
/// that fails is tried again the next time home asks. Everything held is filed under the account that read
/// it, so a listener who signs out and another who signs in do not share an answer.</para>
/// </summary>
public static class OwnerFavourites
{
    private static readonly object Gate = new();

    /// <summary>Who the held answers were read for.</summary>
    private static string _user = "";

    private static readonly Dictionary<string, IReadOnlySet<string>> Held = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Reading = new(StringComparer.Ordinal);

    /// <summary>Raised, on the thread the read finished on, with the owner whose favourites have been
    /// read.</summary>
    public static event Action<string>? Landed;

    /// <summary>
    /// The owner's favourite songs, by RPM id, or null while they have not been read for the account
    /// signed in now. The first ask for an owner starts the read; <see cref="Landed"/> says when it is in.
    /// </summary>
    public static IReadOnlySet<string>? Of(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        if (App.Services.GetService<AuthService>()?.UserId is not { Length: > 0 } user
            || App.Services.GetService<RpmCloudClient>() is not { } cloud)
            return null;

        lock (Gate)
        {
            if (_user != user)
            {
                _user = user;
                Held.Clear();
                Reading.Clear();
            }

            if (Held.TryGetValue(owner, out var held))
                return held;

            if (Reading.Add(owner))
                _ = Task.Run(() => ReadAsync(cloud, user, owner));

            return null;
        }
    }

    private static async Task ReadAsync(RpmCloudClient cloud, string user, string owner)
    {
        HashSet<string>? loved = null;
        try
        {
            var listing = await cloud.ListAsync(CloudShelf.Of(owner));
            loved = new HashSet<string>(
                listing.Where(t => t.Tags is { Favorite: true } && !string.IsNullOrEmpty(t.RpmId)).Select(t => t.RpmId),
                StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // Every failure, a timeout among them (it arrives as a cancellation, and nothing here asked
            // for one): the read is let go, so the next ask tries again.
            RPM.Core.Diagnostics.Scope.Said("home", $"favourites of {owner} not read: {ex.GetType().Name}");
        }

        lock (Gate)
        {
            if (_user != user)
                return;

            Reading.Remove(owner);
            if (loved is null)
                return;

            Held[owner] = loved;
        }

        RPM.Core.Diagnostics.Scope.Said("home", $"{loved.Count} favourite songs of {owner}");
        Landed?.Invoke(owner);
    }
}
