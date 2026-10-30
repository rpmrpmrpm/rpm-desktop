using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Services.Account;

namespace RPM.Desktop.Tests;

/// <summary>
/// Who is shown the beta welcome: an account the server has marked as a closed beta tester, once per
/// account on each machine (<see cref="BetaWelcomeDialog.DueFor"/>).
/// </summary>
public class BetaWelcomeTests
{
    private const string Account = "account-a";

    private static SupporterService.Status Tester => new(true, null, IsTester: true);
    private static SupporterService.Status Supporter => new(true, DateTimeOffset.UtcNow.AddMonths(6), IsTester: false);
    private static SupporterService.Status Free => new(false, null, IsTester: false);

    [Fact]
    public void A_tester_not_yet_welcomed_is_due()
        => Assert.Equal(Account, BetaWelcomeDialog.DueFor(Account, Tester, []));

    [Fact]
    public void A_tester_already_welcomed_is_not_due()
        => Assert.Null(BetaWelcomeDialog.DueFor(Account, Tester, [Account]));

    [Fact]
    public void A_paying_supporter_is_not_due()
        => Assert.Null(BetaWelcomeDialog.DueFor(Account, Supporter, []));

    [Fact]
    public void A_free_account_is_not_due()
        => Assert.Null(BetaWelcomeDialog.DueFor(Account, Free, []));

    /// <summary>
    /// Nothing heard from the server yet is not a tester. The note waits for the answer; the draw that
    /// follows the answer asks again.
    /// </summary>
    [Fact]
    public void An_unknown_standing_is_not_due()
        => Assert.Null(BetaWelcomeDialog.DueFor(Account, null, []));

    [Fact]
    public void Nobody_signed_in_is_not_due()
        => Assert.Null(BetaWelcomeDialog.DueFor(null, Tester, []));

    /// <summary>A second tester on the same machine gets their own welcome.</summary>
    [Fact]
    public void Another_account_welcomed_does_not_count()
        => Assert.Equal(Account, BetaWelcomeDialog.DueFor(Account, Tester, ["account-b"]));
}
