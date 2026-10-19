# ANGLE natives (macOS OpenGL visualizers)

The OpenGL visualizers and Pegasus (Uno `GLCanvasElement`) need ANGLE's `libEGL.dylib` and
`libGLESv2.dylib` on macOS. Uno's Mac GL wrapper (`MacOSNativeOpenGLWrapper`, through `EglHelper`)
opens `libEGL` by name and Uno ships no copy for macOS, so without this pair every visualizer opens
black.

## Where they come from

[kivy/angle-builder](https://github.com/kivy/angle-builder) builds ANGLE from Chromium's source on
GitHub Actions and publishes one archive per architecture as release assets. rpm uses
`angle-macos-arm64.tar.gz` and `angle-macos-x64.tar.gz` from the release named by `ANGLE_RELEASE` in
`scripts/tool-versions.txt`, which also pins the SHA-256 of each file for each RID.

`scripts/fetch-angle.sh <rid>` downloads the archive, checks both files against their pins, and
installs them in `<rid>/` here. The folders are gitignored. A macOS build of `RPM.Desktop` runs the
fetch when a file is missing or differs from its pin (`FetchRpmAngle` in `RPM.Desktop.csproj`), and
the build fails when it cannot get the pinned pair. `RpmSkipAngleFetch=true` skips the download, not
the check.

The build copies the pair beside the executable and nowhere else. A Native AOT app has no deps.json,
so a copy under `runtimes/<rid>/native` is never found in a published build.

## Rules

- **Do not modify them.** No `install_name_tool`, no re-signing outside the release. The arm64 files
  carry the linker's ad hoc signature, and a changed byte makes macOS kill the app on load.
  `release-mac.sh` signs both files with the app's Developer ID.
- Keep `libEGL.dylib` and `libGLESv2.dylib` together. libEGL loads libGLESv2 from its own folder.
- These builds run ANGLE's OpenGL backend (`GL_RENDERER` reads "ANGLE (Apple, ..., OpenGL 4.1
  Metal ...)"). The visualizer workarounds (RGBA8 textures, the unbound unpack buffer, the square
  composite) were found on that backend. A Metal-only ANGLE build (libGDX's `gdx-angle-natives`
  is one) is a backend change and has to be looked at in the app, not only in the tests.

## Changing the version

1. Pick a release of kivy/angle-builder and set `ANGLE_RELEASE` in `scripts/tool-versions.txt`.
2. Run `scripts/fetch-angle.sh osx-arm64 --force` and `scripts/fetch-angle.sh osx-x64 --force`. Each
   refuses the new files and prints the `ANGLE_*_SHA256_*` line to write for each of them. Write the
   four lines, then run both again.
3. Run `AngleTests` in `RPM.Desktop.Tests`: it opens the pair through Uno's own EGL code, draws a
   `#version 300 es` shader and reads it back.
4. Open a visualizer in the app on Apple silicon, and the x64 build under Rosetta.

ANGLE is BSD-3-Clause. The credits (`scripts/credits.json`, id `Angle`) carry its notice and licence
text, which is what the licence asks of a binary redistribution.
