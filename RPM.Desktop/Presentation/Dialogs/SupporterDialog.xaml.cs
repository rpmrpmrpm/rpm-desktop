using RPM.Core.Cloud;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary> The window that asks somebody to support rpm, whatever brought them to it.
///
/// <para> It is the one place where every supporter-only refusal ends up: the DJ, word alignment, the
/// gold backdrop, and a store that has run out of room. One window is used so that the whole offer is
/// always shown. The list of what supporting buys is long and still growing. A refusal that only said
/// "not for you" would miss the moment when somebody is asking about the thing being sold. So every
/// refusal shows the whole offer, which is written once. </para>
///
/// <para><b>What differs is the top of it.</b> A store that has filled up shows two numbers, a bar and
/// a sentence about the sync setting that the app paused. A refused feature shows one sentence naming
/// what was refused. These two cases need different arguments, so they are not one method with optional
/// parts. See <see cref="Occasion"/>, which is the only way in.</para> </summary>
public sealed partial class SupporterDialog : ContentDialog
{
    private SupporterDialog()
    {
        InitializeComponent();
        Shell.AttachResize(this, minWidth: 440, minHeight: 480, maxWidth: 640,
            defaultHeight: 560, defaultWidth: 520);
        KeyDown += OnKeyDown;
    }

    /// <summary> Why this window is up, and everything that differs because of it.
    ///
    /// <para>This is a type, not a few optional parameters, because the two occasions do not share a
    /// shape. <see cref="StorageFull"/> needs numbers that a refusal does not have. <see
    /// cref="Refused"/> needs a sentence that the storage case does not have. With defaulted
    /// parameters, a caller could ask for the storage window, forget the numbers, and the window would
    /// draw an empty meter instead of failing. There is no public constructor. The only way to create
    /// one is to say which occasion it is.</para> </summary>
    public sealed class Occasion
    {
        private Occasion() { }

        internal CloudSpace Space { get; private init; }
        internal bool SyncAllPaused { get; private init; }
        internal bool OfferNeverAgain { get; private init; }

        /// <summary>The sentence naming what was refused, or null for the storage occasion.</summary>
        internal string? Refusal { get; private init; }

        /// <summary>The free store is full and the uploader has stopped.</summary>
        /// <param name="space">What the store holds and what it holds at most, for the line at the
        /// top.</param>
        /// <param name="syncAllPaused">Whether "all my music" was the sync mode when the room ran out.
        /// The line under the opening one is a different sentence in each case. A setting that the app
        /// changed itself has to be passed here, so the window can say so. It is not found later in
        /// Settings.</param>
        /// <param name="offerNeverAgain">Whether to show the "don't show this again" box. False the
        /// first time, because a switch that hides a message the user has not read yet is not an
        /// offer.</param>
        public static Occasion StorageFull(CloudSpace space, bool syncAllPaused, bool offerNeverAgain) =>
            new() { Space = space, SyncAllPaused = syncAllPaused, OfferNeverAgain = offerNeverAgain };

        /// <summary> A supporter-only feature was asked for by somebody who is not a supporter.
        ///
        /// <para>There is no "don't show this again" box here. This window is the answer to a button
        /// the user just pressed, so it is never unsolicited and there is nothing to stop. The storage
        /// notice is the one that appears on its own, so it is the one that can keep showing after it
        /// stops being useful.</para> </summary>
        /// <param name="body">What was refused, in one sentence. See the <c>Supporters_*</c>
        /// strings.</param>
        public static Occasion Refused(string body) => new() { Refusal = body };
    }

    /// <summary> Puts it up over the given tree. </summary>
    /// <param name="root">Null means there is no window to ask in. In that case this returns without
    /// showing anything. A set can be started from the library grid before the player has been
    /// opened.</param>
    /// <returns>True when the user asked never to be shown this again. Only the storage occasion can
    /// offer that, so only it can return true.</returns>
    public static async Task<bool> ShowAsync(XamlRoot? root, Occasion occasion)
    {
        ArgumentNullException.ThrowIfNull(occasion);
        if (root is null) return false;

        var dialog = new SupporterDialog { XamlRoot = root };

        if (occasion.Refusal is { } refused)
        {
            dialog.Shell.Title = Loc.Get("Supporters_Title");
            dialog.BodyText.Text = refused;
        }
        else
        {
            dialog.Shell.Title = Loc.Get("CloudFull_Title");
            dialog.BodyText.Text = Loc.Get("CloudFull_Body");

            dialog.StoragePanel.Visibility = Visibility.Visible;
            dialog.UsageText.Text = Loc.Format("Cloud_UsageFull",
                ByteSize.Human(occasion.Space.UsedBytes), ByteSize.Human(occasion.Space.QuotaBytes));
            dialog.UsageBar.Value = occasion.Space.Known ? occasion.Space.UsedFraction : 1;

            dialog.PausedText.Visibility = Visibility.Visible;
            dialog.PausedText.Text = Loc.Get(
                occasion.SyncAllPaused ? "CloudFull_PausedAll" : "CloudFull_PausedSelected");

            if (occasion.OfferNeverAgain)
                dialog.NeverAgainBox.Visibility = Visibility.Visible;
        }

        await DialogHost.ShowAsync(dialog);

        // Read after the window closes, however it closes. A user who ticks the box and then goes to
        // the store page has still said they do not want to be asked again.
        return dialog.NeverAgainBox.IsChecked == true;
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>The store page where supporting is bought. The window closes when the browser opens.
    /// The app learns the result in the usual way: the store reports "unlimited" on the next quota
    /// read, and the supporter state changes on the next <c>auth/me</c> call.</summary>
    private void Support_Click(object sender, RoutedEventArgs e)
    {
        SupporterLinks.OpenBecomeSupporter();
        Hide();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        Hide();
    }
}
