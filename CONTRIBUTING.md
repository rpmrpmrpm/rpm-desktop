# Contributing to rpm

Thank you for helping. rpm is a music player for your own music. It runs on Windows, macOS, Linux,
iPhone, Apple TV, Android phones and Android TV. The apps are open source; the rpm server that holds
cloud libraries is not.

## Where things go

| You want to | Go to |
| --- | --- |
| Suggest an idea or ask a question | [Discussions](https://github.com/rpmrpmrpm/rpm/discussions) in the `rpm` repo |
| Report a bug | Issues in the repo of the app that has the bug |
| Report a security problem | Do not open an issue. See [SECURITY.md](SECURITY.md). |
| Change code | A pull request in the repo of that app |

The repos:

| Repo | Contains |
| --- | --- |
| [`rpm`](https://github.com/rpmrpmrpm/rpm) | Start here. Discussions, the UI strings and the generators shared by all apps. |
| [`rpm-desktop`](https://github.com/rpmrpmrpm/rpm-desktop) | The desktop app (C#, .NET, Uno Platform) and the shared libraries `RPM.Core` and `RPM.Fingerprint`. |
| [`rpm-apple`](https://github.com/rpmrpmrpm/rpm-apple) | The iPhone and Apple TV apps (Swift, SwiftUI). |
| [`rpm-android`](https://github.com/rpmrpmrpm/rpm-android) | The Android phone and Android TV apps (Kotlin, Jetpack Compose). |

You only need the repo of the app you want to change.

## Before you start

- For a small fix, open a pull request directly.
- For a new feature or a larger change, open a discussion or an issue first, so we can agree on the
  approach before you spend time on it.
- A change that needs something new from the server (a new endpoint or field) starts as an issue.
  Only the maintainers can change the server.

## Pull requests

- Keep a pull request to one change. Small pull requests are reviewed faster.
- Fill in the pull request template: what changes, why, and how you tested it.
- Add or update tests where the code has them.
- Every pull request gets a review from a maintainer. An AI reviewer may also comment. A maintainer
  makes the final decision.
- The maintainers may ask for changes. If a pull request has no activity for 30 days after a
  request, we may close it. You can always reopen it.

### Sign your commits (DCO)

rpm uses the [Developer Certificate of Origin](https://developercertificate.org/) instead of a
contributor licence agreement. By adding a `Signed-off-by` line to a commit, you state that you wrote
the change or have the right to submit it under the project's licence.

```
git commit -s -m "Fix the queue scrolling to the wrong row"
```

This adds `Signed-off-by: Your Name <you@example.com>` using your git name and email. A check on
every pull request fails if a commit is not signed off. To fix older commits on your branch:

```
git rebase --signoff main
git push --force-with-lease
```

## Licence

rpm's code is under the [Mozilla Public License 2.0](LICENSE). Your contribution is under the same
licence. Third-party files keep their own licence, which is stated next to them.

The name "rpm", the logo, the app icons and the wordmark are not covered by the code licence. See
[TRADEMARKS.md](TRADEMARKS.md).

## Money

rpm has paid supporters. Their money pays for the servers, storage and development. Contributions
are voluntary and unpaid, and contributing does not give a share of that money.

## Writing for rpm

### UI text

All text that users see lives in `strings.json` in the [`rpm`](https://github.com/rpmrpmrpm/rpm)
repo, in English, French, Spanish, German and Simplified Chinese. A script generates each app's
string files from it. Do not edit the generated files.

- Write short, plain sentences. Say what happens, not how the code does it.
- Add a `comment` to each new key that tells translators where the text appears.
- If you cannot translate a new string, add the English and say so in the pull request. A maintainer
  will arrange the translations.

### Code comments

Comments are read by people who have not seen the history of the code, and many of them do not have
English as a first language.

- Say what the code does and why, as it is now. Keep warnings about traps ("also set in
  `HomeView.xaml`, change both").
- No history: no dates, names or "used to". That belongs in the commit message.
- Plain international English: short sentences, common words, no idioms. Use British spelling
  (colour, centre, catalogue, licence, behaviour).
- Keep identifiers as they are, and use the code's own names for things.

### Commit messages

Use the form `area: what changes`, for example `queue: keep the playing row in view`. Explain in the
body why the change is needed if that is not obvious.

## Building and testing the desktop app

See the README for the build commands. Before you open a pull request:

- `dotnet build` must finish with no new warnings. The analyzers in `RPM.Analyzers` check rpm's
  own rules and report them as warnings or errors.
- Run the tests of the projects you changed.
- `RPM.Core` and `RPM.Fingerprint` are also built by the rpm server. Do not change a public type or
  the format of stored data without discussing it first.
