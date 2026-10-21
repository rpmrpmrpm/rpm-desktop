# rpm for desktop

The rpm music player for Windows, macOS and Linux, and the libraries the apps share with the rpm
server.

Part of [rpm](https://github.com/rpmrpmrpm/rpm). Ideas and questions go to the
[discussions](https://github.com/rpmrpmrpm/rpm/discussions) there.

## What is here

| Folder | What it is |
| --- | --- |
| `RPM.Desktop` | The app. C# and XAML on [Uno Platform](https://platform.uno), one code base for all three systems. |
| `RPM.Core` | Library, playback, metadata, analysis. Also used by the rpm server. |
| `RPM.Fingerprint` | Audio fingerprints and the record keys the apps and the server share. |
| `RPM.Cast` | Google Cast (a modified copy of SharpCaster, MIT). |
| `RPM.DataGrid` | The track grid control. |
| `RPM.Onnx` | Runs the on-device analysis models. |
| `RPM.Analyzers` | Roslyn analyzers that check rpm's own code rules at build time. |
| `tools/` | Developer tools. |
| `*.Tests` | Unit tests. |

## Build

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). An IDE is optional: Visual
Studio, Rider and VS Code all work.

```
dotnet build RPM.Desktop/RPM.Desktop.csproj -f net10.0-desktop
dotnet run --project RPM.Desktop/RPM.Desktop.csproj -f net10.0-desktop
```

The first build downloads the analysis models (about 214 MB, from `updates.rpmrpmrpm.com`) and the
tools it needs from GitHub releases: ffmpeg (this repo), fpcalc (`acoustid/chromaprint`) and the
ANGLE graphics library (`kivy/angle-builder`). Behind a firewall, allow `updates.rpmrpmrpm.com`,
`github.com` and `objects.githubusercontent.com`.

Some tests read files that all apps share, such as `strings.json`. Clone the
[`rpm`](https://github.com/rpmrpmrpm/rpm) repo next to this one for them:

```
git clone https://github.com/rpmrpmrpm/rpm
git clone https://github.com/rpmrpmrpm/rpm-desktop
```

The icon font (Font Awesome 5 Pro) is licensed and cannot be published, so it is not in the repo.
Without it the app works and some icons are blank; the build says so.

A build from source uses the same settings, sign-in and libraries as an installed rpm on the same
computer (the `RPM` folder in the local application data folder). Quit the installed rpm before you
run your build, and expect your build to work on your real library.

The sharing tests listen on the local network. On Windows the first `dotnet test` run brings up a
Windows Firewall prompt for `testhost`. The tests pass whether you allow it or press Cancel.

Run the tests:

```
dotnet test RPM.Core.Tests
dotnet test RPM.Fingerprint.Tests
dotnet test RPM.DataGrid.Tests
```

The local library, playback, analysis, casting and sharing on your home network work without an
account. The cloud library, catalogue pages and recognition need an rpm account.

## Licence

[Mozilla Public License 2.0](LICENSE). Third-party files keep their own licence. The name and logo
are reserved: see [TRADEMARKS.md](TRADEMARKS.md).
