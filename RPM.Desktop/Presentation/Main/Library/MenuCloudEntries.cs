namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>Which of the cloud entries a right-click menu shows, and whether the record can be brought
/// home. Everything else on both menus is always shown.</summary>
/// <param name="KeepOnComputer">Bring a record that is only in the cloud down onto this disk.</param>
/// <param name="AddToCloud">Put this in the cloud.</param>
/// <param name="CanUpload">Whether the store has room for what "add to cloud" would send. False greys the
/// entry out rather than removing it. A menu that loses a line makes the user think they misremembered
/// it. The user needs to know the offer exists and is out of reach.</param>
public readonly record struct CloudMenuEntries(
    bool KeepOnComputer, bool AddToCloud, bool CanUpload = true)
{
    /// <summary>Whether any of them appear at all. This decides whether the separator above them is
    /// shown.</summary>
    public bool Any => KeepOnComputer || AddToCloud;

    /// <summary>Whether "add to cloud" is live. It is shown but disabled when the store is full.</summary>
    public bool AddToCloudEnabled => AddToCloud && CanUpload;
}

/// <summary>
/// The cloud half of the two right-click menus. This is the half that changes.
///
/// <para>
/// One place is used because the record's menu and the song's menu must agree. A listener who adds an
/// album from the wall, and then right-clicks one of its songs, must see the same words with the same
/// icons. They must mean the same thing for the same file. Each menu must offer only what the engine can
/// do. For example, "add to cloud" is not offered in a mode where nothing is chosen by hand, because the
/// engine cannot keep that offer.
/// </para>
///
/// <para>Removing a copy from the cloud is not here. It is one of the three entries in the Delete
/// submenu, where the listener can see in one place what each copy costs them. See
/// <see cref="RPM.Core.Library.DeleteOffer"/>.</para>
/// </summary>
public static class MenuCloudEntries
{
    /// <param name="selectedMode">Whether this machine is in "selected items" mode. In "all" mode the
    /// track is already going up, and in "none" mode nothing is. So the entry means something only in
    /// selected mode, where picking a record by hand decides what is sent.</param>
    /// <param name="canAdd">Some of the record is not yet in the cloud.</param>
    /// <param name="isCloudOnly">Its bytes are not on this disk. This is read outside the mode check on
    /// purpose. Bringing a record home does not depend on what this machine chooses to send up. A second
    /// install may have no sync mode set at all.</param>
    /// <param name="canUpload">Whether the store has room. This greys out only "add to cloud". Making
    /// room is what the Delete submenu's "Delete from cloud" is for, so a full store must not disable
    /// that entry. A single "cloud is unavailable" flag would make exactly that mistake.</param>
    public static CloudMenuEntries For(
        bool selectedMode, bool canAdd, bool isCloudOnly = false, bool canUpload = true)
        => new(
            KeepOnComputer: isCloudOnly,
            AddToCloud: selectedMode && canAdd,
            CanUpload: canUpload);

    /// <summary>No cloud entries at all. This is what a record borrowed from somebody else gets. Their
    /// storage is theirs, and "bring it down onto this computer" is not possible for a file this machine
    /// does not own.</summary>
    public static readonly CloudMenuEntries None =
        new(KeepOnComputer: false, AddToCloud: false, CanUpload: false);
}
