#!/usr/bin/env bash
#
# rpm: builds the macOS .app bundle.
#
# What this does: publishes the app bundle, registers the rpm:// URL scheme
# in it, checks that the scheme is really there, and (optionally) signs it.
#
# It also adds the Velopack updater, builds the .dmg, notarises and staples
# both, writes the update feed for this architecture and, with --publish, uploads
# everything to updates.rpmrpmrpm.com. The .nupkg is built by hand (step 7).
# The files go to an S3 bucket behind CloudFront.
#
# The .dmg is a plain one: the app, a link to Applications, and no artwork. A
# styled window needs a background image and a prepared .DS_Store file (for
# example made with dmgbuild), and updating does not depend on it.
#
# Why the scheme registration matters
# -----------------------------------
#
# rpm connects a Discogs account through ASWebAuthenticationSession (see
# RPM.Desktop/Services/MacWebAuth.cs). The accounts API finishes the OAuth flow by
# redirecting the browser to rpm://discogs?status=..., and the sign-in sheet
# watches for that scheme and closes itself.
#
# The sheet intercepts the redirect internally, so the flow also works when the
# scheme is not registered. That is the problem: a bundle without the registration
# looks fine in testing, and fails only when LaunchServices receives an rpm:// URL
# that has no owner (a link clicked outside the sheet, or a redirect that escapes
# it). So this script treats the registration as a release requirement and stops
# without it.
#
# Two ordering rules, both required:
#   1. PlistBuddy MUST run before codesign. Info.plist is sealed into the
#      signature; editing it afterwards invalidates the seal and Gatekeeper
#      rejects the app.
#   2. The scheme is read from MacWebAuth.cs and not written here a second time.
#      A bundle that registers a scheme the app does not listen for is as wrong
#      as a bundle that registers nothing, and harder to see.
#
# Usage:
#   ./scripts/release-mac.sh [--arch arm64|x64] [--sign] [--version X.Y.Z]
#                            [--dmg] [--skip-notarize] [--publish] [--first-release | --no-delta]
#
#   --sign   Codesign with the Developer ID identity and the hardened runtime.
#            Without it the bundle is unsigned: fine to run locally, but it
#            cannot be distributed.
#   --dmg    Build the .dmg and the update feed. Implies --sign: an unsigned
#            bundle must not be given to anybody.
#   --skip-notarize  Build the .dmg without sending it or the .app to Apple.
#            For trying the steps out. The result cannot be distributed, and
#            Gatekeeper rejects it on another machine.
#   --publish  Upload the feed and the .dmg to the update host. Needs AWS
#            credentials with write access to the bucket; see RPM_UPDATE_BUCKET
#            below. The version is the one in the rpm-v<X.Y.Z> tag on HEAD, the
#            tag that the Windows workflow built from, so --version is refused.
#            origin must have that tag at the same commit, and the checkout
#            must have no changed or untracked files, or the Mac build is not what
#            the tag names (scripts/release_version.py checks this). To publish
#            while the working tree has other changes:
#            git worktree add <folder> rpm-v<X.Y.Z>
#            and run the script from <folder>. The worktree must sit next to the
#            rpm and rpm-fonts-pro checkouts, as this one does. Nothing else
#            needs to be copied in: the build downloads every model, tool and
#            ANGLE file that it ships.
#
# What the build needs besides this checkout:
#   ../rpm             the shared files (strings.json), at the commit to release.
#   ../rpm-fonts-pro   the icon fonts. A release build fails without them.
#   RPM_APP_KEY        the official API key. When it is not set, it is read from
#                      Secrets Manager (RPM/AppKey, Current) with the AWS
#                      credentials that --publish uses.
#   RPM_PROVISION_PROFILE  the Developer ID provisioning profile (see PROFILE
#                      below). It is not in the repository. The default is
#                      RPM.Desktop/RPM.Desktop.provisionprofile, which git ignores.
#   RPM_SIGN_IDENTITY  the codesign identity. The default is the one Developer
#                      ID Application identity of team TEAM_ID in the keychain.
#   --version  The version of a build that is not published. Required with
#            --dmg. Without --dmg the default is 0.0.0 (the version in the
#            csproj, which no release uses).
#   --first-release  The channel has never been published, so there is no
#            feed to add to and no package to make a delta from. Without it,
#            a --dmg build stops when the live feed cannot be read.
#   --no-delta  Add this release to the live feed as a full package only. Use it
#            when step 7 stops because UpdateMac cannot apply the delta. This
#            happens when the new package has a file in a folder that the
#            previous package does not have (for example a new
#            Resources/<language>.lproj). Installed copies then download the
#            full package once. --first-release would do the same but would
#            empty the feed.

set -euo pipefail

ARCH="$(uname -m)"; [ "${ARCH}" = "aarch64" ] && ARCH="arm64"
[ "${ARCH}" = "x86_64" ] && ARCH="x64"
SIGN=false
DMG=false
NOTARIZE=true
PUBLISH=false
FIRST_RELEASE=false
NO_DELTA=false
VERSION=""

while [ $# -gt 0 ]; do
    case "$1" in
        --arch)    ARCH="$2"; shift 2 ;;
        --sign)    SIGN=true; shift ;;
        --dmg)     DMG=true; SIGN=true; shift ;;
        --skip-notarize) NOTARIZE=false; shift ;;
        --publish) PUBLISH=true; DMG=true; SIGN=true; shift ;;
        --first-release) FIRST_RELEASE=true; shift ;;
        --no-delta) NO_DELTA=true; shift ;;
        --version) VERSION="$2"; shift 2 ;;
        *) echo "unknown option: $1"; exit 2 ;;
    esac
done

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"          # the rpm-desktop checkout
WORKSPACE_DIR="$(cd "${REPO_DIR}/.." && pwd)"       # the folder that holds it and ../rpm

# The version.
#
# A published release takes its version from its tag, so that one version number
# always names one commit. release_version.py reads the one rpm-v tag on HEAD, and
# refuses unless origin has it at this commit and the checkout has no change that
# the tag does not have.
if [ "${PUBLISH}" = true ]; then
    if [ -n "${VERSION}" ]; then
        echo "FATAL: --publish takes the version from the rpm-v tag on HEAD. Leave out --version."
        exit 2
    fi
    VERSION="$(python3 -I "${SCRIPT_DIR}/release_version.py" head --repo "${REPO_DIR}" --remote origin)" || exit 1
elif [ -z "${VERSION}" ]; then
    # A .dmg build writes a feed, so it must say which version it is. A default version
    # here could repeat an old number, which installed copies ignore or downgrade to.
    if [ "${DMG}" = true ]; then
        echo "FATAL: --version is required with --dmg (--publish reads it from the tag)."
        exit 2
    fi
    VERSION="0.0.0"
fi
# Check the form on every path: CFBundleVersion in the Info.plist takes whole numbers only.
python3 -I "${SCRIPT_DIR}/release_version.py" check "${VERSION}" > /dev/null || exit 2

case "${ARCH}" in
    arm64|x64) ;;
    *) echo "FATAL: --arch must be arm64 or x64 (got '${ARCH}')"; exit 2 ;;
esac

CSPROJ="${REPO_DIR}/RPM.Desktop/RPM.Desktop.csproj"
ENTITLEMENTS="${REPO_DIR}/RPM.Desktop/RPM.Desktop.entitlements"
MACWEBAUTH="${REPO_DIR}/RPM.Desktop/Services/Account/MacWebAuth.cs"
UPDATECHANNEL="${REPO_DIR}/RPM.Desktop/Services/Updates/UpdateChannel.cs"
RELEASE_DELTA="${SCRIPT_DIR}/release_delta.py"
PUBLISH_DIR="/private/tmp/rpm-build-${VERSION}-${ARCH}"
APP_NAME="rpm.app"
APP_PATH="${PUBLISH_DIR}/${APP_NAME}"
# The App ID that the iPhone app also has. The Developer ID provisioning profile below
# is made for it, and Apple matches a profile to an app by this id.
BUNDLE_ID="com.rpm.player"
TEAM_ID="LBW775Z8JG"
RID="osx-${ARCH}"
NOTARY_PROFILE="${RPM_NOTARY_PROFILE:-fobo-notarytool}"

# The codesign identity: the Developer ID Application certificate of TEAM_ID. It is
# found in the keychain by team, so the repository does not name the person it was
# issued to. Set RPM_SIGN_IDENTITY when the keychain holds more than one.
IDENTITY_APP="${RPM_SIGN_IDENTITY:-}"
if [ "${SIGN}" = true ] && [ -z "${IDENTITY_APP}" ]; then
    IDENTITY_APP="$(security find-identity -v -p codesigning \
        | sed -nE "s/.*\"(Developer ID Application: .* \(${TEAM_ID}\))\".*/\1/p" | sort -u)"
    if [ -z "${IDENTITY_APP}" ] || [ "$(printf '%s\n' "${IDENTITY_APP}" | wc -l)" -ne 1 ]; then
        echo "FATAL: expected one Developer ID Application identity for team ${TEAM_ID} in the keychain."
        echo "       Found: ${IDENTITY_APP:-none}. Set RPM_SIGN_IDENTITY to the one to use."
        exit 1
    fi
fi

# The Developer ID provisioning profile for ${BUNDLE_ID}. It authorises the
# keychain entitlement, which a Developer ID app may only have together with the
# profile: if the app is signed without the profile, the system kills it at launch.
# The profile is made for the Developer ID Application certificate, so a renewed
# certificate needs a new profile. Step 3c checks that the two match.
PROFILE="${RPM_PROVISION_PROFILE:-${REPO_DIR}/RPM.Desktop/RPM.Desktop.provisionprofile}"

# The name that the app uses for itself towards the updater. It is written into the bundle
# (sq.version), into the package, and into the feed, and all three must agree, or
# the app reads a feed about a different application.
#
# This is not the bundle id. The package id is the name by which every installed copy,
# on the Mac and on Windows (.github/workflows/release.yml), knows itself and reads its
# feed, so it must not change when the bundle id changes.
PACK_ID="com.rpm.desktop"
PACK_TITLE="rpm"
CHANNEL="osx-${ARCH}"
NUPKG_NAME="${PACK_ID}-${VERSION}-${CHANNEL}-full.nupkg"
DELTA_NAME="${PACK_ID}-${VERSION}-${CHANNEL}-delta.nupkg"
DMG_NAME="rpm-${ARCH}.dmg"
ARTIFACTS_DIR="${PUBLISH_DIR}/artifacts"

# Where the feed is served from. The bucket is private and CloudFront reads it
# through an origin access control. This script writes to the bucket, and the
# app reads https://updates.rpmrpmrpm.com/<channel>/.
UPDATE_BUCKET="${RPM_UPDATE_BUCKET:-rpm-updates}"
UPDATE_DISTRIBUTION="${RPM_UPDATE_DISTRIBUTION:-}"

# Velopack's native Mac updater, included in the vpk global tool. The bundle
# must contain it, with an sq.version file next to it, or UpdateManager.IsInstalled is
# false and the app never reads the feed. Its version must match the
# Velopack library in Directory.Packages.props.
if [ "${DMG}" = true ]; then
    UPDATE_MAC="$(find "${HOME}/.dotnet/tools/.store/vpk" -path '*/vendor/UpdateMac' 2>/dev/null | sort -V | tail -1)"
    if [ -z "${UPDATE_MAC}" ] || [ ! -f "${UPDATE_MAC}" ]; then
        echo "FATAL: UpdateMac not found under ~/.dotnet/tools/.store/vpk. Install vpk:" >&2
        echo "       dotnet tool install -g vpk --version 0.0.1298" >&2
        exit 1
    fi
    # vpk makes the delta package in step 7.
    if ! command -v vpk > /dev/null; then
        echo "FATAL: vpk is not on PATH. Install it:" >&2
        echo "       dotnet tool install -g vpk --version 0.0.1298" >&2
        exit 1
    fi
    # The host that installed copies read their feed from, as named in the app. The
    # previous release is read from there, so the delta is made from exactly the
    # package that those copies have.
    UPDATE_HOST="$(sed -nE 's/.*const string Host = "([^"]+)".*/\1/p' "${UPDATECHANNEL}" | head -1)"
    if [ -z "${UPDATE_HOST}" ]; then
        echo "FATAL: could not read Host from ${UPDATECHANNEL}." >&2
        exit 1
    fi
fi

# Step 1: the scheme, read from the app source.
# The app decides which scheme it listens for. This script only copies it. If the
# constant is renamed and this sed no longer matches, the release stops. That is
# intended.
SCHEME="$(sed -nE 's/.*CallbackScheme = "([^"]+)".*/\1/p' "${MACWEBAUTH}" | head -1)"
if [ -z "${SCHEME}" ]; then
    echo "FATAL: could not read CallbackScheme from ${MACWEBAUTH}."
    echo "       The URL scheme must come from the app, not from this script."
    exit 1
fi
echo "[1/5] URL scheme from MacWebAuth.cs: ${SCHEME}://"

# Step 2: publish the bundle, unsigned.
# The bundle is unsigned here on purpose, even when --sign is passed: Uno's
# GenerateAppBundle runs its UNOB0018 extended-attributes check only when signing,
# and macOS 15 and later puts an unremovable com.apple.provenance xattr on every
# file, so Uno cannot sign on this OS. The finished bundle is signed in step 4,
# where the real codesign accepts the provenance attribute.
# The bundle is assembled here and not by `-p:PackageFormat=app`.
#
# Uno's GenerateAppBundle does more than sort files: it compiles a small native
# launcher and links it against coreclr. A Native AOT build has no coreclr (the
# app IS the executable), so that link fails with
# "ld: library 'coreclr' not found", and the publish writes a bundle with no
# program in it. There is no option to change this, because the task assumes a
# managed app.
#
# Apart from that, it only moves files, which is done below. Everything the app
# ships goes into Contents/MacOS, flat, exactly as in a publish folder: the binary
# reads its assets by a path relative to AppContext.BaseDirectory, so the layout
# that runs is the layout that ships. Contents/Resources holds the icon and the
# folders that codesign does not accept in Contents/MacOS (see below), each
# reached through a symlink at its publish-folder path.
echo "[2/5] dotnet publish (${RID}, Native AOT, unsigned)..."
rm -rf "${PUBLISH_DIR}"

# The previous release, read before anything is built.
#
# Step 7 makes the delta from the newest full package in the live feed and
# writes the new feed as that feed plus this release (scripts/release_delta.py).
# It is read here, at the start, so that a version that is not newer than the
# feed's version stops the run before the build and before Apple's notarisation.
PREVIOUS_DIR="${PUBLISH_DIR}/previous"
PREVIOUS_FULL=""
if [ "${DMG}" = true ]; then
    if [ "${FIRST_RELEASE}" = true ]; then
        python3 -I "${RELEASE_DELTA}" previous --feed "${UPDATE_HOST}/${CHANNEL}/" --channel "${CHANNEL}" \
            --version "${VERSION}" --out "${PREVIOUS_DIR}" --first-release
        echo "      --first-release: no previous ${CHANNEL} release, so no delta"
    elif [ "${NO_DELTA}" = true ]; then
        python3 -I "${RELEASE_DELTA}" previous --feed "${UPDATE_HOST}/${CHANNEL}/" --channel "${CHANNEL}" \
            --version "${VERSION}" --out "${PREVIOUS_DIR}" --no-delta
        echo "      --no-delta: the live ${CHANNEL} feed is kept, and this release is a full package only"
    else
        PREVIOUS_FULL="$(python3 -I "${RELEASE_DELTA}" previous --feed "${UPDATE_HOST}/${CHANNEL}/" \
            --channel "${CHANNEL}" --version "${VERSION}" --out "${PREVIOUS_DIR}")"
        echo "      previous release: $(basename "${PREVIOUS_FULL}") (SHA-256 checked against the feed)"
    fi
fi
PAYLOAD_DIR="${PUBLISH_DIR}/payload"
# Version and ApplicationDisplayVersion, the same pair that the Windows workflow passes:
# Uno's SDK sets Version from ApplicationDisplayVersion, so passing only one of them
# leaves the other at the value in the csproj. RpmUpdates turns on in-place updates, which
# a copy built from source does not have (UpdateChannel.Released).
#
# A release build needs the official API key (RpmAppKeySource in RPM.Core.csproj). The
# AWS credentials that publish the release can read it, so it is not kept on this Mac.
if [ -z "${RPM_APP_KEY:-}" ]; then
    RPM_APP_KEY="$(aws secretsmanager get-secret-value --region eu-west-1 --secret-id RPM/AppKey \
        --query SecretString --output text \
        | python3 -I -c 'import json, sys; print(json.load(sys.stdin)["Current"])')" \
        || { echo "FATAL: RPM_APP_KEY is not set and RPM/AppKey could not be read from Secrets Manager."; exit 1; }
fi
export RPM_APP_KEY
dotnet publish "${CSPROJ}" \
    -c Release -f net10.0-desktop -r "${RID}" \
    -p:SelfContained=true \
    -p:RpmUpdates=true \
    -p:Version="${VERSION}" \
    -p:ApplicationDisplayVersion="${VERSION}" \
    -o "${PAYLOAD_DIR}" 2>&1 | tail -15

if [ ! -x "${PAYLOAD_DIR}/rpm" ]; then
    echo "FATAL: ${PAYLOAD_DIR}/rpm was not produced."
    echo "       Without it there is nothing to put in the bundle. Look for an"
    echo "       ILC or linker error above."
    exit 1
fi

# The symbols: kept, but not put in the bundle.
#
# Native AOT writes rpm.dSYM next to the binary. If it were copied into the .app, the
# symbols would be shipped to every user. Kept here, it turns the offsets in a
# native crash report (rpm.Admin, Errors, Crashes) back into function names:
# atos -o rpm.dSYM/Contents/Resources/DWARF/rpm -arch arm64 -l <load address> <address>.
mkdir -p "${ARTIFACTS_DIR}"
SYMBOLS_ZIP="${ARTIFACTS_DIR}/rpm-${VERSION}-${RID}.dSYM.zip"
if [ -d "${PAYLOAD_DIR}/rpm.dSYM" ]; then
    ditto -c -k --keepParent "${PAYLOAD_DIR}/rpm.dSYM" "${SYMBOLS_ZIP}"
    rm -rf "${PAYLOAD_DIR}/rpm.dSYM"
    echo "      symbols: ${SYMBOLS_ZIP}"
    # A DMG can be given to someone without --publish, and ${ARTIFACTS_DIR} is under
    # /private/tmp, which is deleted by the next build of this version and by a restart.
    # So every DMG build also keeps its symbols here. --publish adds the copy in S3.
    if [ "${DMG}" = true ]; then
        SYMBOLS_KEEP="${RPM_SYMBOLS_DIR:-${HOME}/Library/Developer/rpm/symbols}/${VERSION}"
        mkdir -p "${SYMBOLS_KEEP}"
        cp "${SYMBOLS_ZIP}" "${SYMBOLS_KEEP}/"
        echo "      kept   : ${SYMBOLS_KEEP}/$(basename "${SYMBOLS_ZIP}")"
    fi
else
    echo "WARNING: no rpm.dSYM in ${PAYLOAD_DIR}; native crash reports from this build cannot be symbolicated."
    SYMBOLS_ZIP=""
fi

echo "[2/5] assemble ${APP_NAME}"
mkdir -p "${APP_PATH}/Contents/MacOS" "${APP_PATH}/Contents/Resources"
# -R so the Assets, model and font folders come across whole.
cp -R "${PAYLOAD_DIR}/." "${APP_PATH}/Contents/MacOS/"
chmod +x "${APP_PATH}/Contents/MacOS/rpm"
# Each staging copy is about 850 MB and nothing reads it after this point. Keeping all of
# them would need about 4 GB of disk.
rm -rf "${PAYLOAD_DIR}"

# Contents/MacOS holds Mach-O files and symlinks. Data goes to Contents/Resources.
#
# This is the layout that Uno's own bundler produces, and codesign depends on it in
# two ways:
#   * A folder in Contents/MacOS whose name has a dot (Uno.Fonts.OpenSans,
#     Assets.xcassets) is read as a nested bundle. It has no Info.plist, and
#     codesign --deep stops with "bundle format unrecognized, invalid, or
#     unsuitable".
#   * Any other file in Contents/MacOS that is not Mach-O, in a subfolder or not,
#     is signed as code, with its signature in extended attributes. The update
#     package is a zip, which drops extended attributes, so the installed update
#     would fail codesign --verify on every font, model and image.
# Each data file and each folder with only data is moved to Contents/Resources, and a
# relative symlink is left at its old path, so a path relative to
# AppContext.BaseDirectory still works. A folder with only Mach-O files stays,
# because codesign --deep signs those in place (for example the ANGLE dylibs, which
# are placed next to the binary). A folder that mixes the two cannot be handled either
# way and stops the build.
# This runs before the Resources links below, which then point at these links.
is_macho() { case "$(file -b "$1")" in Mach-O*) return 0 ;; *) return 1 ;; esac; }
( cd "${APP_PATH}/Contents/MacOS" || exit 1
  for entry in *; do
      if [ -d "${entry}" ]; then
          has_code=false; has_data=false
          while IFS= read -r f; do
              if is_macho "${f}"; then has_code=true; else has_data=true; fi
          done < <(find "${entry}" -type f)
          if [ "${has_code}" = true ] && [ "${has_data}" = true ]; then
              echo "FATAL: Contents/MacOS/${entry} holds both Mach-O and data files."
              echo "       codesign needs the first in Contents/MacOS and the second in"
              echo "       Contents/Resources. Split it in the publish output."
              exit 1
          fi
          [ "${has_code}" = true ] && continue
      elif [ -f "${entry}" ]; then
          is_macho "${entry}" && continue
      else
          continue
      fi
      mv "${entry}" "../Resources/${entry}"
      ln -s "../Resources/${entry}" "${entry}"
  done )

# Contents/MacOS/Resources: where Uno looks for ms-appx: assets.
#
# Two roots have to work, and they are different:
#
#   * The code of the app reads "Assets/..." relative to AppContext.BaseDirectory
#     (the fonts, the ONNX models, the startup sound, the genre photographs).
#     This is the flat layout above, which is also how a publish folder runs.
#   * Uno resolves ms-appx:///Assets/... to <BaseDirectory>/Resources/Assets/...
#     when the binary is inside a bundle. Without that folder, every font in
#     the app fails to load and the window is drawn in the system font.
#
# These are symlinks and not a second copy, because the models alone are half a gigabyte.
# The links are relative and point at siblings, not at the folder that contains them,
# because a link that points to its own folder makes every tool that walks this tree
# (ditto, hdiutil, notarytool) recurse without end.
mkdir -p "${APP_PATH}/Contents/MacOS/Resources"
( cd "${APP_PATH}/Contents/MacOS" || exit 1
  for d in */; do
      d="${d%/}"
      [ "${d}" = "Resources" ] && continue
      ln -sfn "../${d}" "Resources/${d}"
  done )

# LSMinimumSystemVersion 15.0 is needed by the local network prompt in step 2b.
# CFBundleIconFile names the file that step 2a writes into Resources.
# NSMicrophoneUsageDescription is for vinyl recording
# (Services/Audio/OpenAlCapture.cs): without it, macOS ends the process the first
# time the app opens an audio input. The entitlements contain the matching
# com.apple.security.device.audio-input, which the hardened runtime requires.
# CFBundleVersion is also the release version. Launch Services compares it to choose
# between copies of the app on one disk, including which copy opens rpm:// links.
# The permission sentences come from scripts/strings.json: the English one here,
# and every language in InfoPlist.strings in step 2b.
MICROPHONE_LINE="$(python3 "${WORKSPACE_DIR}/rpm/scripts/infoplist_strings.py" en NSMicrophoneUsageDescription)"
if [ -z "${MICROPHONE_LINE}" ]; then
    echo "FATAL: no microphone sentence in scripts/strings.json (Mac_MicrophoneUsage)."
    exit 1
fi
cat > "${APP_PATH}/Contents/Info.plist" <<PLIST_EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key><string>en-US</string>
    <key>CFBundleDisplayName</key><string>${PACK_TITLE}</string>
    <key>CFBundleExecutable</key><string>rpm</string>
    <key>CFBundleIconFile</key><string>icon.icns</string>
    <key>CFBundleIdentifier</key><string>${BUNDLE_ID}</string>
    <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
    <key>CFBundleName</key><string>${PACK_TITLE}</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>${VERSION}</string>
    <key>CFBundleVersion</key><string>${VERSION}</string>
    <key>LSMinimumSystemVersion</key><string>15.0</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSMicrophoneUsageDescription</key><string>${MICROPHONE_LINE}</string>
    <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>
PLIST_EOF


# Read the value back and check it: a bundle whose CFBundleExecutable
# names a file that does not exist installs without error and does not open.
EXE="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "${APP_PATH}/Contents/Info.plist")"
if [ ! -x "${APP_PATH}/Contents/MacOS/${EXE}" ]; then
    echo "FATAL: ${APP_PATH}/Contents/MacOS/${EXE} is missing or not executable."
    exit 1
fi

# Check the fonts in the way that Uno will ask for them. A bundle that fails this
# starts and draws, but with the wrong font and without the icons.
if [ ! -f "${APP_PATH}/Contents/MacOS/Resources/Assets/Fonts/fa-solid-900.ttf" ]; then
    echo "FATAL: Contents/MacOS/Resources/Assets/Fonts is not reachable."
    echo "       Uno resolves every ms-appx: asset through there; without it the"
    echo "       app opens with no font it asked for."
    exit 1
fi

# Every link in the bundle must reach a file or folder. find -L lists a link as
# type l only when its target does not exist.
BROKEN_LINKS="$(find -L "${APP_PATH}/Contents" -type l)"
if [ -n "${BROKEN_LINKS}" ]; then
    echo "FATAL: broken symlinks in the bundle:"
    echo "${BROKEN_LINKS}"
    exit 1
fi

echo "      ✓ ${APP_PATH} ($(du -sh "${APP_PATH}" | cut -f1))"

PLIST="${APP_PATH}/Contents/Info.plist"

# On a cross-build, silently using the architecture of the host would ship the wrong binary.
BUILT_ARCHS="$(lipo -archs "${APP_PATH}/Contents/MacOS/${EXE}" 2>/dev/null || true)"
case "${ARCH}" in
    arm64) echo "${BUILT_ARCHS}" | grep -q "arm64"  || { echo "FATAL: expected arm64, got '${BUILT_ARCHS}'"; exit 1; } ;;
    x64)   echo "${BUILT_ARCHS}" | grep -q "x86_64" || { echo "FATAL: expected x86_64, got '${BUILT_ARCHS}'"; exit 1; } ;;
esac
echo "      ✓ arch: ${BUILT_ARCHS}"

# ffmpeg and fpcalc, run from the bundle.
#
# The publish fails unless both tools are present with the SHA-256 that tool-versions.txt
# pins (PublishRpmTools in RPM.Desktop.csproj). This check looks at what the bundle
# holds: both in Contents/MacOS, built for this architecture, and each one runs and
# prints the pinned version. Without them the app can decode and fingerprint nothing on
# a Mac without Homebrew. On the Mac that built the bundle, ToolPaths finds the copy from
# Homebrew, and the bundle would look fine.
#
# The check runs again after codesign: the signature is written into each binary, and a
# binary that the hardened runtime refuses to start would otherwise fail first at the
# first import of a user. An x64 bundle built on Apple silicon runs its tools under
# Rosetta. Without Rosetta this stops, because this machine could not run that release.
TOOLS_LIPO_ARCH="arm64"; [ "${ARCH}" = "x64" ] && TOOLS_LIPO_ARCH="x86_64"
TOOLS_WANT_FFMPEG="$(. "${SCRIPT_DIR}/tool-versions.txt"; printf 'ffmpeg version %s-rpm%s ' "${FFMPEG_VERSION}" "${RPM_FFMPEG_BUILD}")"
TOOLS_WANT_FPCALC="$(. "${SCRIPT_DIR}/tool-versions.txt"; printf 'fpcalc version %s ' "${CHROMAPRINT_VERSION}")"

check_tool() {   # $1 tool name, $2 start of its first -version line, $3 when
    local path="${APP_PATH}/Contents/MacOS/$1" out first
    if [ ! -x "${path}" ]; then
        echo "FATAL: Contents/MacOS/$1 is missing or not executable ($3)."
        echo "       An app without it cannot decode or fingerprint anything."
        exit 1
    fi
    if ! lipo "${path}" -verify_arch "${TOOLS_LIPO_ARCH}" 2>/dev/null; then
        echo "FATAL: Contents/MacOS/$1 is not built for ${TOOLS_LIPO_ARCH} ($(lipo -archs "${path}" 2>&1))."
        exit 1
    fi
    # Read the whole output and then cut it, instead of piping to head: if head closes the
    # pipe early, the tool can end with SIGPIPE, which pipefail reports as a failure of the tool.
    if ! out="$("${path}" -version 2>&1)"; then
        echo "FATAL: Contents/MacOS/$1 -version failed ($3):"
        echo "${out}" | sed 's/^/       /'
        exit 1
    fi
    first="${out%%$'\n'*}"
    case "${first}" in
        "$2"*) echo "      ✓ $1 ($3): ${first}" ;;
        *) echo "FATAL: Contents/MacOS/$1 says '${first}' ($3); tool-versions.txt pins '$2'."; exit 1 ;;
    esac
}

check_tools() {   # $1 when
    check_tool ffmpeg "${TOOLS_WANT_FFMPEG}" "$1"
    check_tool fpcalc "${TOOLS_WANT_FPCALC}" "$1"
}

check_tools "as published"

# Step 2a: the icon, in the shape that macOS uses for app icons.
#
# The publish writes the icon that Resizetizer built from Assets/Icons/icon.png, which
# is the phone's tile. Its corner is rounder than the corner macOS uses, so it looks
# smaller than the icons next to it in the Dock.
# Assets/Icons/AppIcon.icns is the same document rendered by actool for macOS,
# and it replaces the bundled icon under the file name that the plist already has.
# This runs before codesign, for the same reason as the plist edits.
echo "[2a/5] the macOS icon into the bundle"
ICNS_SRC="${REPO_DIR}/RPM.Desktop/Assets/Icons/AppIcon.icns"
ICNS_NAME="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIconFile' "${APP_PATH}/Contents/Info.plist" 2>/dev/null || true)"
case "${ICNS_NAME}" in
    "")      echo "      (no CFBundleIconFile in the bundle, left alone)" ;;
    *.icns)  ;;
    *)       ICNS_NAME="${ICNS_NAME}.icns" ;;
esac
if [ -n "${ICNS_NAME}" ] && [ -f "${ICNS_SRC}" ]; then
    cp "${ICNS_SRC}" "${APP_PATH}/Contents/Resources/${ICNS_NAME}"
    echo "      ✓ Contents/Resources/${ICNS_NAME}"
elif [ ! -f "${ICNS_SRC}" ]; then
    echo "FATAL: ${ICNS_SRC} is missing. Run scripts/make_app_icon.py on a Mac with Xcode."
    exit 1
fi

# Step 2b: the local network, and the sentence that macOS shows before it allows it.
#
# macOS 15 requires a permission for every local network connection of an app
# (mDNS, and the sockets that follow it), which the user grants once. Without
# NSLocalNetworkUsageDescription the app is not asked and not allowed: it is
# denied, silently and permanently. For the user this looks like a home with no
# other computers and no speakers.
#
# The key is set here and not in the project, because the Info.plist of the bundle is
# produced by the publish. It must be written BEFORE codesign, for the same
# reason as the scheme: the plist is sealed into the signature.
echo "[2b/5] declare the local network in Info.plist"
/usr/libexec/PlistBuddy -c "Delete :NSLocalNetworkUsageDescription" "${PLIST}" 2>/dev/null || true
/usr/libexec/PlistBuddy \
    -c "Add :NSLocalNetworkUsageDescription string $(python3 "${WORKSPACE_DIR}/rpm/scripts/infoplist_strings.py" en NSLocalNetworkUsageDescription)" \
    "${PLIST}" > /dev/null

# Both permission sentences in every language of the app, as
# Contents/Resources/<lang>.lproj/InfoPlist.strings. macOS reads the file for the
# language of the user and falls back to Info.plist, which is in English.
python3 "${WORKSPACE_DIR}/rpm/scripts/infoplist_strings.py" write "${APP_PATH}/Contents/Resources"
for lang in en fr es de zh-Hans; do
    if ! plutil -lint -s "${APP_PATH}/Contents/Resources/${lang}.lproj/InfoPlist.strings"; then
        echo "FATAL: ${lang}.lproj/InfoPlist.strings is not a valid strings file."
        exit 1
    fi
done
echo "      ✓ permission sentences in en, fr, es, de, zh-Hans"

# Read the value back and check it: a denied local network is not visible
# at run time, so it must be caught here.
NETWORK_LINE="$(/usr/libexec/PlistBuddy -c "Print :NSLocalNetworkUsageDescription" "${PLIST}" 2>/dev/null || true)"
if [ -z "${NETWORK_LINE}" ]; then
    echo "FATAL: NSLocalNetworkUsageDescription is not in the bundle."
    echo "       Sharing and casting would both find nothing on macOS 15, with"
    echo "       no error anywhere."
    exit 1
fi
echo "      ✓ ${NETWORK_LINE}"

# Step 3: register the scheme, BEFORE codesign.
echo "[3/5] register ${SCHEME}:// in Info.plist"
/usr/libexec/PlistBuddy -c "Delete :CFBundleURLTypes" "${PLIST}" 2>/dev/null || true
/usr/libexec/PlistBuddy \
    -c "Add :CFBundleURLTypes array" \
    -c "Add :CFBundleURLTypes:0 dict" \
    -c "Add :CFBundleURLTypes:0:CFBundleURLName string ${BUNDLE_ID}" \
    -c "Add :CFBundleURLTypes:0:CFBundleURLSchemes array" \
    -c "Add :CFBundleURLTypes:0:CFBundleURLSchemes:0 string ${SCHEME}" \
    "${PLIST}" > /dev/null

# Read the scheme back from the built bundle, and do not rely on the write above
# having returned 0.
verify_scheme() {
    local when="$1" found
    found="$(/usr/libexec/PlistBuddy -c "Print :CFBundleURLTypes:0:CFBundleURLSchemes:0" "${PLIST}" 2>/dev/null || true)"
    if [ "${found}" != "${SCHEME}" ]; then
        echo "FATAL: ${SCHEME}:// is not registered in the bundle (${when})."
        echo "       Read back: '${found:-<nothing>}'"
        echo "       rpm cannot be released without it: LaunchServices would"
        echo "       treat ${SCHEME}:// as unowned."
        exit 1
    fi
    echo "      ✓ ${SCHEME}:// registered (${when})"
}
verify_scheme "after PlistBuddy"

# Step 3b: the updater that the app gives the swap to, and the marker file that says the
# app is installed. This runs BEFORE codesign, because both are sealed into the signature.
#
# UpdateMac is the process that replaces the bundle after the app has exited.
# sq.version makes UpdateManager.IsInstalled true. Without it the app decides that it
# is not installed and never reads the feed, which looks like a release that nobody
# has published.
#
# Velopack reads sq.version at Contents/MacOS, but the file itself is in
# Contents/Resources, with a relative symlink at that path. In Contents/MacOS it
# would be signed through extended attributes, the update zip drops them, and every
# updated copy would fail codesign --verify and Gatekeeper.
# This is the layout that vpk pack writes from Velopack 1.0.1 (velopack#705); both
# locators and UpdateMac read through the link.
if [ "${DMG}" = true ]; then
    echo "[3b] inject UpdateMac + sq.version (channel ${CHANNEL})"
    MACOS_DIR="${APP_PATH}/Contents/MacOS"
    cp "${UPDATE_MAC}" "${MACOS_DIR}/UpdateMac"
    chmod +x "${MACOS_DIR}/UpdateMac"
    cat > "${APP_PATH}/Contents/Resources/sq.version" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
<metadata>
<id>${PACK_ID}</id>
<title>${PACK_TITLE}</title>
<description>${PACK_TITLE}</description>
<authors>rpm</authors>
<version>${VERSION}</version>
<channel>${CHANNEL}</channel>
<mainExe>${EXE}</mainExe>
<os>osx</os>
<rid>${RID}</rid>
<machineArchitecture>${ARCH}</machineArchitecture>
</metadata>
</package>
EOF
    ln -s ../Resources/sq.version "${MACOS_DIR}/sq.version"
    echo "      ✓ Contents/MacOS/UpdateMac + sq.version (in Contents/Resources, linked)"
fi

# Step 3c: the provisioning profile and the keychain entitlements.
#
# The session (the sign-in tokens) is stored in the data protection keychain
# (RPM.Desktop/Services/Account/MacKeychainStore.cs), which needs the
# keychain-access-groups entitlement. A Developer ID app may have it only with
# an embedded profile that authorises it. So the profile is checked here, before
# anything is signed, and copied into the bundle, where the signature seals it.
#
# The entitlements go on the signature of the app itself only, never through --deep:
# --deep would also put them on ffmpeg, fpcalc and UpdateMac, and a tool that has a
# restricted entitlement without a profile of its own is killed when it starts. So
# the keychain entitlements are not in RPM.Desktop.entitlements. They are added to
# a copy of it here, from TEAM_ID and BUNDLE_ID, and that copy signs the app in
# step 4, after --deep has signed everything inside it with the plain file.
#
# Without --sign nothing happens here: an unsigned bundle has no keychain and
# keeps the session in the sealed file, as a Debug build does.
APP_ENTITLEMENTS="${PUBLISH_DIR}/rpm.app.entitlements"
if [ "${SIGN}" = true ]; then
    echo "[3c] provisioning profile + keychain entitlements"
    if [ ! -f "${PROFILE}" ]; then
        echo "FATAL: ${PROFILE} is missing."
        echo "       Without it the keychain entitlement cannot be signed in, and with the"
        echo "       entitlement but without the profile the app is killed at launch."
        exit 1
    fi
    PROFILE_PLIST="${PUBLISH_DIR}/profile.plist"
    security cms -D -i "${PROFILE}" > "${PROFILE_PLIST}"
    IDENTITY_SHA1="$(security find-certificate -c "${IDENTITY_APP}" -Z | awk '/SHA-1 hash:/ { print $3 }')"
    if ! python3 - "${PROFILE_PLIST}" "${TEAM_ID}" "${BUNDLE_ID}" "${IDENTITY_SHA1}" <<'PY'
import datetime, hashlib, plistlib, sys
path, team, bundle, identity = sys.argv[1:]
p = plistlib.load(open(path, "rb"))
e = p.get("Entitlements", {})
problems = []
if e.get("com.apple.application-identifier") != f"{team}.{bundle}":
    problems.append(f"it is for {e.get('com.apple.application-identifier')}, not {team}.{bundle}")
if not p.get("ProvisionsAllDevices"):
    problems.append("it is not a Developer ID profile (ProvisionsAllDevices is not set)")
if "OSX" not in p.get("Platform", []):
    problems.append(f"it is not for macOS ({p.get('Platform')})")
if p["ExpirationDate"].replace(tzinfo=datetime.timezone.utc) <= datetime.datetime.now(datetime.timezone.utc):
    problems.append(f"it expired on {p['ExpirationDate']:%Y-%m-%d}")
groups = e.get("keychain-access-groups", [])
if f"{team}.*" not in groups and f"{team}.{bundle}" not in groups:
    problems.append(f"it does not allow the keychain group {team}.{bundle} ({groups})")
if identity.upper() not in [hashlib.sha1(c).hexdigest().upper() for c in p.get("DeveloperCertificates", [])]:
    problems.append("it was not made with the signing certificate (renewed? make a new profile)")
for line in problems:
    print(f"FATAL: the provisioning profile: {line}.")
sys.exit(1 if problems else 0)
PY
    then
        exit 1
    fi
    cp "${PROFILE}" "${APP_PATH}/Contents/embedded.provisionprofile"

    cp "${ENTITLEMENTS}" "${APP_ENTITLEMENTS}"
    /usr/libexec/PlistBuddy \
        -c "Add :com.apple.application-identifier string ${TEAM_ID}.${BUNDLE_ID}" \
        -c "Add :com.apple.developer.team-identifier string ${TEAM_ID}" \
        -c "Add :keychain-access-groups array" \
        -c "Add :keychain-access-groups:0 string ${TEAM_ID}.${BUNDLE_ID}" \
        "${APP_ENTITLEMENTS}" > /dev/null
    echo "      ✓ profile for ${TEAM_ID}.${BUNDLE_ID}, embedded"
fi

# Check that the keychain entitlement is on the app and on nothing inside it. This runs
# after codesign. The first condition is the purpose of step 3c, and the second is what
# --deep would break.
verify_keychain_entitlement() {
    local app_has
    app_has="$(codesign -d --entitlements - --xml "${APP_PATH}" 2>/dev/null | grep -c "keychain-access-groups" || true)"
    if [ "${app_has}" = "0" ]; then
        echo "FATAL: the app's signature has no keychain-access-groups."
        echo "       The session would go to the sealed file instead of the keychain."
        exit 1
    fi
    local tool
    for tool in ffmpeg fpcalc UpdateMac; do
        [ -e "${APP_PATH}/Contents/MacOS/${tool}" ] || continue
        if codesign -d --entitlements - --xml "${APP_PATH}/Contents/MacOS/${tool}" 2>/dev/null \
            | grep -qE "keychain-access-groups|application-identifier"; then
            echo "FATAL: Contents/MacOS/${tool} carries the keychain entitlement."
            echo "       With no profile of its own it would be killed when it starts."
            exit 1
        fi
    done
    echo "      ✓ keychain entitlement on the app only"
}

# Step 4: sign.
if [ "${SIGN}" = true ]; then
    echo "[4/5] strip detritus + codesign (--deep, hardened runtime)"
    if [ ! -f "${ENTITLEMENTS}" ]; then
        echo "FATAL: ${ENTITLEMENTS} is missing. Signing without the JIT"
        echo "       entitlements produces an app the kernel SIGKILLs at launch."
        exit 1
    fi
    # codesign rejects FinderInfo and ResourceFork as "detritus". The provenance attribute
    # cannot be removed on macOS 15 and later, but codesign accepts it.
    find "${APP_PATH}" -exec xattr -d com.apple.FinderInfo   {} \; 2>/dev/null || true
    find "${APP_PATH}" -exec xattr -d com.apple.ResourceFork {} \; 2>/dev/null || true
    # Check the layout rule from step 2, which step 3b keeps too: no data file in
    # Contents/MacOS, sq.version included.
    STRAY_DATA="$(find "${APP_PATH}/Contents/MacOS" -type f \
        -exec sh -c 'file -b "$1" | grep -q "^Mach-O" || echo "$1"' _ {} \;)"
    if [ -n "${STRAY_DATA}" ]; then
        echo "FATAL: data files in Contents/MacOS would be signed into extended attributes:"
        echo "${STRAY_DATA}"
        exit 1
    fi
    codesign --force --deep --options runtime --timestamp \
        --entitlements "${ENTITLEMENTS}" -s "${IDENTITY_APP}" "${APP_PATH}"
    # Sign the app itself again, without --deep: its own executable with the keychain
    # entitlements, and the seal of the bundle over everything already signed inside it.
    codesign --force --options runtime --timestamp \
        --entitlements "${APP_ENTITLEMENTS}" -s "${IDENTITY_APP}" "${APP_PATH}"
    codesign --verify --strict --verbose=2 "${APP_PATH}" 2>&1 | tail -2
    verify_keychain_entitlement

    # Proves that rule 1 held: if PlistBuddy had run after signing, the seal would be
    # broken and these checks would fail.
    verify_scheme "after codesign"
    check_tools "after codesign"
else
    echo "[4/5] --sign not passed: bundle is UNSIGNED (local use only)"
fi

# Step 5: notarise and staple the .app.
#
# The app is stapled and not only notarised: a stapled ticket is read from the disk, so
# the first launch also works on a machine that is offline or behind a firewall that
# blocks Apple's OCSP.
if [ "${DMG}" = true ] && [ "${NOTARIZE}" = true ]; then
    echo "[5] notarize + staple the .app"
    APP_ZIP="${PUBLISH_DIR}/rpm-${ARCH}-notarize.zip"
    ditto -c -k --keepParent "${APP_PATH}" "${APP_ZIP}"
    xcrun notarytool submit "${APP_ZIP}" --keychain-profile "${NOTARY_PROFILE}" --wait
    rm -f "${APP_ZIP}"
    xcrun stapler staple "${APP_PATH}"
    xcrun stapler validate "${APP_PATH}" || { echo "FATAL: the .app is not stapled"; exit 1; }
    spctl --assess --type execute --verbose "${APP_PATH}" || { echo "FATAL: the .app fails Gatekeeper"; exit 1; }
    echo "      ✓ signed, notarized, stapled"
elif [ "${DMG}" = true ]; then
    echo "[5] --skip-notarize: the .app is signed and NOT notarized. Not distributable."
fi

# Step 6: the .dmg.
#
# Plain: the app and a link to Applications. The name has no version, so the download
# link on the website never has to change for a release. The version is in the feed
# and in the app.
if [ "${DMG}" = true ]; then
    echo "[6] build ${DMG_NAME}"
    mkdir -p "${ARTIFACTS_DIR}"
    DMG_PATH="${ARTIFACTS_DIR}/${DMG_NAME}"
    DMG_STAGE="${PUBLISH_DIR}/dmg-stage"
    rm -rf "${DMG_STAGE}" "${DMG_PATH}"
    mkdir -p "${DMG_STAGE}"
    ditto "${APP_PATH}" "${DMG_STAGE}/${APP_NAME}"
    ln -s /Applications "${DMG_STAGE}/Applications"
    hdiutil create -quiet -volname "rpm" -srcfolder "${DMG_STAGE}" -ov -format UDZO "${DMG_PATH}"
    rm -rf "${DMG_STAGE}"
    [ -f "${DMG_PATH}" ] || { echo "FATAL: hdiutil produced no ${DMG_NAME}"; exit 1; }
    codesign -s "${IDENTITY_APP}" -f --timestamp "${DMG_PATH}"

    if [ "${NOTARIZE}" = true ]; then
        xcrun notarytool submit "${DMG_PATH}" --keychain-profile "${NOTARY_PROFILE}" --wait
        xcrun stapler staple "${DMG_PATH}"
        xcrun stapler validate "${DMG_PATH}" || { echo "FATAL: the .dmg is not stapled"; exit 1; }
        spctl --assess --type install --verbose "${DMG_PATH}" || { echo "FATAL: the .dmg fails Gatekeeper"; exit 1; }
    fi
    echo "      ✓ ${DMG_NAME} ($(du -h "${DMG_PATH}" | cut -f1))"
fi

# Step 7: the update package, its delta, and the feed.
#
# The layout is the one that `vpk pack` writes: the CONTENTS of the .app under
# lib/app/Contents, plus the OPC skeleton. It is built by hand and not by vpk,
# because vpk wants to build the bundle itself, and everything above (the scheme, the
# plist, the icon, the signature, the ticket) has already been done to this bundle.
#
# The .app in the package is the SIGNED AND STAPLED one, so the copy that an update
# installs is as distributable as the copy in the .dmg.
#
# The delta holds the files that changed since the previous release, as patches,
# so an installed copy that still has the package of that release downloads tens of
# megabytes and not the whole app. The delta is only kept when UpdateMac, the updater
# that the app itself runs, can rebuild from it a package with exactly the files of the
# full package.
if [ "${DMG}" = true ]; then
    echo "[7] build ${NUPKG_NAME}"
    NUPKG_STAGE="${PUBLISH_DIR}/nupkg-stage"
    rm -rf "${NUPKG_STAGE}"
    mkdir -p "${NUPKG_STAGE}/lib/app" "${NUPKG_STAGE}/_rels"
    ditto "${APP_PATH}/Contents" "${NUPKG_STAGE}/lib/app/Contents"

    # Velopack packages cannot hold symlinks. A link is stored as a file named
    # <link>.__symlink whose content is the target, and UpdateMac recreates the
    # link from it when it applies the update (vendor/UpdateMac, bundle.rs). Otherwise
    # zip would follow the Contents/MacOS/Resources links from step 2 and store
    # every asset twice, and an update would create real folders where the signed
    # bundle has links.
    find "${NUPKG_STAGE}/lib/app" -type l | while IFS= read -r link; do
        target="$(readlink "${link}")"
        rm "${link}"
        printf '%s' "${target}" > "${link}.__symlink"
    done
    if [ -n "$(find "${NUPKG_STAGE}/lib/app" -type l)" ]; then
        echo "FATAL: symlinks left in the package stage."
        exit 1
    fi

    cat > "${NUPKG_STAGE}/${PACK_ID}.nuspec" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
<metadata>
<id>${PACK_ID}</id>
<title>${PACK_TITLE}</title>
<description>${PACK_TITLE}</description>
<authors>rpm</authors>
<version>${VERSION}</version>
<channel>${CHANNEL}</channel>
<mainExe>${EXE}</mainExe>
<os>osx</os>
<rid>${RID}</rid>
<machineArchitecture>${ARCH}</machineArchitecture>
</metadata>
</package>
EOF

    cat > "${NUPKG_STAGE}/_rels/.rels" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Type="http://schemas.microsoft.com/packaging/2010/07/manifest" Target="/${PACK_ID}.nuspec" Id="R1" />
</Relationships>
EOF

    {
        echo '<?xml version="1.0" encoding="utf-8"?>'
        echo '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
        echo '  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />'
        echo '  <Default Extension="nuspec" ContentType="application/octet" />'
        find "${NUPKG_STAGE}/lib/app" -type f -name '*.*' \
            | sed 's/.*\.//' | tr '[:upper:]' '[:lower:]' | sort -u \
            | while IFS= read -r ext; do
                case "${ext}" in rels|nuspec|'') continue ;; esac
                printf '  <Default Extension="%s" ContentType="application/octet" />\n' "${ext}"
            done
        echo '</Types>'
    } > "${NUPKG_STAGE}/[Content_Types].xml"

    NUPKG_PATH="${ARTIFACTS_DIR}/${NUPKG_NAME}"
    rm -f "${NUPKG_PATH}"
    ( cd "${NUPKG_STAGE}" && zip -qrX "${NUPKG_PATH}" '[Content_Types].xml' _rels "${PACK_ID}.nuspec" lib )
    rm -rf "${NUPKG_STAGE}"

    echo "      ✓ ${NUPKG_NAME} ($(du -h "${NUPKG_PATH}" | cut -f1))"

    DELTA_PATH="${ARTIFACTS_DIR}/${DELTA_NAME}"
    rm -f "${DELTA_PATH}"
    if [ -n "${PREVIOUS_FULL}" ]; then
        echo "[7b] build ${DELTA_NAME} from $(basename "${PREVIOUS_FULL}")"
        vpk delta generate --base "${PREVIOUS_FULL}" --new "${NUPKG_PATH}" --output "${DELTA_PATH}" 2>&1 \
            | grep -E "Delta processed|ERR|FTL" || true
        [ -f "${DELTA_PATH}" ] || { echo "FATAL: vpk wrote no ${DELTA_NAME}."; exit 1; }

        # The check that matters: the package that UpdateMac rebuilds from the delta, as an
        # installed copy will do, has the same files as the full package. The paths are
        # absolute, because UpdateMac changes to its own folder before it reads its arguments.
        REBUILT="${PUBLISH_DIR}/rebuilt-${VERSION}.nupkg"
        if ! "${UPDATE_MAC}" patch --old "${PREVIOUS_FULL}" --delta "${DELTA_PATH}" --output "${REBUILT}" \
            > "${PUBLISH_DIR}/patch.log" 2>&1; then
            echo "FATAL: UpdateMac could not apply ${DELTA_NAME} to $(basename "${PREVIOUS_FULL}"):"
            tail -20 "${PUBLISH_DIR}/patch.log" | sed 's/^/       /'
            echo "       A \"new file\" just before the error is a file in a folder the previous package"
            echo "       lacks, which UpdateMac cannot write. Publish this version with --no-delta."
            exit 1
        fi
        python3 -I "${RELEASE_DELTA}" same-files "${NUPKG_PATH}" "${REBUILT}" | sed 's/^/      ✓ rebuilt by UpdateMac: /'
        rm -f "${REBUILT}"
        echo "      ✓ ${DELTA_NAME} ($(du -h "${DELTA_PATH}" | cut -f1))"

        python3 -I "${RELEASE_DELTA}" feed --previous "${PREVIOUS_DIR}/releases.${CHANNEL}.json" \
            --channel "${CHANNEL}" --full "${NUPKG_PATH}" --delta "${DELTA_PATH}" --out "${ARTIFACTS_DIR}" \
            | sed 's/^/      ✓ /'
        # About 500 MB that nothing reads after this point.
        rm -f "${PREVIOUS_FULL}"
    else
        python3 -I "${RELEASE_DELTA}" feed --previous "${PREVIOUS_DIR}/releases.${CHANNEL}.json" \
            --channel "${CHANNEL}" --full "${NUPKG_PATH}" --no-delta --out "${ARTIFACTS_DIR}" \
            | sed 's/^/      ✓ /'
    fi
fi

# Step 8: publish.
#
# The package is uploaded first and the feed last. A feed that names a package that
# cannot be downloaded yet makes every app that reads it fail in between. The other
# order only makes the release appear a few seconds later.
if [ "${PUBLISH}" = true ]; then
    # The signed feed is made first, before anything is uploaded. The private key goes
    # from Secrets Manager into the tool through the pipe and is never written to
    # disk. FeedSign checks what it signed against the public key compiled into the
    # app, so a wrong key stops the release here.
    echo "[8] sign the feed"
    aws secretsmanager get-secret-value --region eu-west-1 --secret-id RPM/UpdateFeedKey \
            --query SecretString --output text \
        | dotnet run -c Release --project "${REPO_DIR}/tools/FeedSign" -- "${CHANNEL}" \
            "${ARTIFACTS_DIR}/releases.${CHANNEL}.json" "${ARTIFACTS_DIR}" \
        | sed 's/^/      ✓ /' \
        || { echo "FATAL: could not sign the feed; nothing was uploaded"; exit 1; }

    echo "[8] upload to s3://${UPDATE_BUCKET}/${CHANNEL}/"
    # The symbols go to the private deploy bucket and not to the public update host.
    if [ -n "${SYMBOLS_ZIP}" ]; then
        aws s3 cp "${SYMBOLS_ZIP}" "s3://fobo-deploy-artifacts/rpm-symbols/${VERSION}/$(basename "${SYMBOLS_ZIP}")" --only-show-errors \
            || { echo "FATAL: could not store the symbols; a release without them cannot be symbolicated"; exit 1; }
    fi
    aws s3 cp "${NUPKG_PATH}" "s3://${UPDATE_BUCKET}/${CHANNEL}/${NUPKG_NAME}" --only-show-errors
    if [ -f "${DELTA_PATH}" ]; then
        aws s3 cp "${DELTA_PATH}" "s3://${UPDATE_BUCKET}/${CHANNEL}/${DELTA_NAME}" --only-show-errors
    fi
    aws s3 cp "${DMG_PATH}"   "s3://${UPDATE_BUCKET}/latest/${DMG_NAME}" --only-show-errors
    aws s3 cp "${ARTIFACTS_DIR}/RELEASES-${CHANNEL}" "s3://${UPDATE_BUCKET}/${CHANNEL}/RELEASES-${CHANNEL}" --only-show-errors
    aws s3 cp "${ARTIFACTS_DIR}/releases.${CHANNEL}.json" "s3://${UPDATE_BUCKET}/${CHANNEL}/releases.${CHANNEL}.json" \
        --cache-control "no-cache" --only-show-errors
    # Copies that support signed feeds read this one (see FeedSignature). The plain
    # feed above is for older copies.
    aws s3 cp "${ARTIFACTS_DIR}/releases.${CHANNEL}.signed.json" \
        "s3://${UPDATE_BUCKET}/${CHANNEL}/releases.${CHANNEL}.signed.json" \
        --cache-control "no-cache" --only-show-errors

    # The feed and the .dmg are the two files that CloudFront must not keep serving
    # from its cache. The packages have the version in their name and are never
    # replaced, so they can stay in the cache.
    if [ -n "${UPDATE_DISTRIBUTION}" ]; then
        aws cloudfront create-invalidation --distribution-id "${UPDATE_DISTRIBUTION}" \
            --paths "/${CHANNEL}/releases.${CHANNEL}.json" "/${CHANNEL}/releases.${CHANNEL}.signed.json" \
                "/${CHANNEL}/RELEASES-${CHANNEL}" "/latest/${DMG_NAME}" \
            --query 'Invalidation.Id' --output text
    else
        echo "      (no RPM_UPDATE_DISTRIBUTION set: CloudFront was not invalidated,"
        echo "       so the feed is whatever the edge has until it expires)"
    fi
    echo "      ✓ published"
fi

# Step 9: report.
echo "[9] done"
echo ""
echo "  bundle  : ${APP_PATH}"
echo "  scheme  : ${SCHEME}://  (registered + verified)"
echo "  signed  : ${SIGN}"
echo "  version : ${VERSION}  (channel ${CHANNEL})"
if [ "${DMG}" = true ]; then
    echo "  dmg     : ${DMG_PATH}"
    echo "  package : ${NUPKG_PATH}"
    if [ -f "${DELTA_PATH}" ]; then
        echo "  delta   : ${DELTA_PATH} ($(du -h "${DELTA_PATH}" | cut -f1))"
    else
        echo "  delta   : none (--first-release)"
    fi
    echo "  feed    : ${ARTIFACTS_DIR}/releases.${CHANNEL}.json"
    if [ "${PUBLISH}" = true ]; then
        echo "  signed  : ${ARTIFACTS_DIR}/releases.${CHANNEL}.signed.json"
    fi
fi
echo "  published: ${PUBLISH}"
echo ""
echo "  Try the scheme:  open ${SCHEME}://discogs?status=connected"
echo "  (registers with LaunchServices once the bundle has been run once)"
