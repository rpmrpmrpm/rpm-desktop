# rpm (desktop app)

An app that cleans up a messy music folder: reads embedded tags, identifies unknown
tracks by Chromaprint audio fingerprint, then renames or moves everything into a clean
Artist/Album layout. It uses Uno Platform (net10.0-desktop, Skia) with a shell and a wizard region.

## Wizard flow

`MainPage` (pick music folder, destination, Move/Copy, naming format)
→ `AnalyzePage` (scan → tags → fingerprint identify, progress + cancel)
→ `PreviewPage` (accept/skip each proposed rename)
→ `OrganizePage` (apply with progress)
→ `DonePage` (summary, failures, open folder).

Routes and DI live in `App.xaml.cs`. Models are plain INotifyPropertyChanged
(`Presentation/Models/`). Core logic (scanner, tag reader,
naming templates, rename planner, applier, analysis orchestrator) is in
`RPM.Core`, with unit tests in `RPM.Core.Tests`.

## Identification

`Services/IdentificationProvider` picks the backend:

- Development: set `ACOUSTID_API_KEY` (acoustid.org/new-application). The client then calls AcoustID and
  MusicBrainz directly. `fpcalc` (chromaprint) must be on PATH: `brew install chromaprint`.
- Production: the rpm server. It looks in its own fingerprint database first, then AcoustID, then AudD
  (paid with rpm credits). `LambdaIdentificationBackend` is the client for it.
- Neither configured → tags + filename guessing only (a notice shows on the main page).

## Strings

UI strings come from `../scripts/strings.json` (en/fr/es/de/zh-Hans). Regenerate the
`.resw` files with `python3 scripts/gen_locales.py`. Never hand-edit `Strings/*/Resources.resw`.

## rpm account

`Services/AuthService` signs into accounts.rpmrpmrpm.com;
the shell header shows the account chip. Credits pay for AudD identification.
