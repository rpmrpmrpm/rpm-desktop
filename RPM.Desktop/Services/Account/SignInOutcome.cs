using System.Net;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Account;

/// <summary>Why the accounts service said no to a sign-in.</summary>
public enum SignInRefusal
{
    /// <summary>The email and password do not match an account.</summary>
    WrongPassword,

    /// <summary>The account exists, but its email address has not been confirmed yet.</summary>
    NotConfirmed,

    /// <summary>The browser was opened, and sign-in was not finished there in time.</summary>
    BrowserTimedOut,

    /// <summary>Any other refusal. The service's own sentence is in the log.</summary>
    Other,
}

/// <summary>
/// How a sign-in attempt ended, as the sign-in dialog shows it.
///
/// <para>This is a closed set, so neither an exception's message nor the accounts service's English
/// text can reach the dialog. No case carries text. <see cref="AuthService"/> writes what the service
/// or the exception said to the log before one of these is returned.</para>
/// </summary>
public abstract record SignInOutcome
{
    private SignInOutcome() { }

    public sealed record SignedIn : SignInOutcome;

    /// <summary>Cancelled by the person. Nothing is shown.</summary>
    public sealed record Abandoned : SignInOutcome;

    public sealed record Refused(SignInRefusal Why) : SignInOutcome;

    /// <summary>The request did not complete.</summary>
    public sealed record Failed(Fault Fault) : SignInOutcome;

    /// <summary>
    /// Reads the accounts service's refusal of <c>auth/login</c>.
    ///
    /// <para>The login endpoint returns 401 for a wrong password and 400 for an unconfirmed address,
    /// and it sends no error code. Its only other 400 is a missing email or password. The dialog never
    /// sends those (see <c>Login_NeedFields</c>). So on this endpoint the status code alone is exact.</para>
    /// </summary>
    public static Refused FromLoginStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => new Refused(SignInRefusal.WrongPassword),
        HttpStatusCode.BadRequest => new Refused(SignInRefusal.NotConfirmed),
        _ => new Refused(SignInRefusal.Other),
    };

    /// <summary>The sentence the dialog shows, or null when there is nothing to say.</summary>
    public string? StringKey => this switch
    {
        SignedIn or Abandoned => null,
        Refused { Why: SignInRefusal.WrongPassword } => "Login_WrongPassword",
        Refused { Why: SignInRefusal.NotConfirmed } => "Login_NotConfirmed",
        Refused { Why: SignInRefusal.BrowserTimedOut } => "Login_BrowserTimedOut",
        Refused => "Login_Unable",

        // Being offline or slow is worth saying as such. A refused token, or a server error in the
        // middle of signing in, means "could not sign in", not "you have been signed out".
        Failed { Fault: Fault.Offline or Fault.Timeout } failed => failed.Fault.StringKey(),
        Failed { Fault: Fault.None } => null,
        _ => "Login_Unable",
    };
}
