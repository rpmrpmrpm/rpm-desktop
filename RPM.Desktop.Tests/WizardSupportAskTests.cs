using RPM.Desktop.Presentation.Setup.Steps;
using RPM.Desktop.Services.Account;

namespace RPM.Desktop.Tests;

/// <summary>
/// Who the setup's last step asks for support (<see cref="WizardDoneStep.AsksForSupport"/>): an account
/// the server has said is not a supporter, and nobody signed in. Never an account whose standing is not
/// known.
/// </summary>
public class WizardSupportAskTests
{
    private static SupporterService.Status Free => new(false, null, IsTester: false);
    private static SupporterService.Status Supporter => new(true, DateTimeOffset.UtcNow.AddMonths(6), IsTester: false);
    private static SupporterService.Status Tester => new(true, null, IsTester: true);

    [Fact]
    public void A_free_account_is_asked()
        => Assert.True(WizardDoneStep.AsksForSupport(signedIn: true, Free));

    [Fact]
    public void A_supporter_is_not_asked()
        => Assert.False(WizardDoneStep.AsksForSupport(signedIn: true, Supporter));

    [Fact]
    public void A_closed_beta_tester_is_not_asked()
        => Assert.False(WizardDoneStep.AsksForSupport(signedIn: true, Tester));

    /// <summary>The request failed or has not come back. Asking would tell a supporter they do not
    /// support rpm.</summary>
    [Fact]
    public void An_account_with_an_unknown_standing_is_not_asked()
        => Assert.False(WizardDoneStep.AsksForSupport(signedIn: true, null));

    [Fact]
    public void Nobody_signed_in_is_asked()
        => Assert.True(WizardDoneStep.AsksForSupport(signedIn: false, null));
}
