using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RPM.Desktop.Services.Platform;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// The question asked when a speaker is picked while Windows Firewall is blocking rpm: allow rpm
/// through, or cancel the cast. Without it the cast starts, but the speaker cannot fetch the stream.
/// About 20 seconds later the menu only says that the speaker did not start playing.
///
/// <para>Asked only for <see cref="FirewallStanding.Blocked"/>. With no rule yet
/// (<see cref="FirewallStanding.NotDecided"/>), Windows shows its own prompt when rpm starts listening.
/// Asking here as well would be two questions for one decision, unless the last cast already failed
/// that way. "Block all incoming connections" (<see cref="FirewallStanding.BlocksEverything"/>) cannot
/// be fixed by a rule. The cast menu's firewall line says so and offers Windows Security instead.</para>
/// </summary>
public static class FirewallDialog
{
    /// <summary>
    /// Reads the firewall and, when it is blocking rpm, asks. Returns whether to go on with the cast:
    /// false when the person cancels here or at the Windows prompt. When the change was made but did
    /// not hold, the cast goes on, fails as it did before, and the menu's firewall line then says the
    /// firewall still blocks rpm.
    /// </summary>
    /// <param name="root">The tree to ask over. Null casts without asking, so the menu's firewall
    /// line is the only report.</param>
    /// <param name="speaker">The speaker's name, as the menu lists it.</param>
    /// <param name="afterNoFetch">The last cast ended with a speaker that never fetched the stream, so
    /// no rule at all counts as blocked (<see cref="FirewallJudge.AfterNoFetch"/>). This is not
    /// optional. The menu's line already reads the firewall that way. A question that read it
    /// differently would stay quiet under a line saying rpm is blocked.</param>
    public static async Task<bool> BeforeCastingAsync(XamlRoot? root, string speaker, bool afterNoFetch)
    {
        if (root is null)
            return true;

        var standing = await WindowsFirewall.ReadAsync();
        if (afterNoFetch)
            standing = FirewallJudge.AfterNoFetch(standing);
        if (standing != FirewallStanding.Blocked)
            return true;

        var answer = await PromptDialog.ShowAsync(
            root,
            Loc.Get("Firewall_AskTitle"),
            Loc.Format("Firewall_AskBody", speaker),
            primaryText: Loc.Get("Firewall_AskAllow"),
            closeText: Loc.Get("Common_Cancel"),
            defaultButton: ContentDialogButton.Primary);

        if (answer != ContentDialogResult.Primary)
            return false;

        return await WindowsFirewall.AllowAsync() != FirewallChange.Declined;
    }
}
