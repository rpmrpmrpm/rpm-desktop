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

The first build downloads the analysis models and tools (about 200 MB).

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
