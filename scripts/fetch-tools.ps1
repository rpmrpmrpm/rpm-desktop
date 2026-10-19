<#
.SYNOPSIS
  Fetches the ffmpeg + fpcalc binaries the app shells out to, for one RID.

.DESCRIPTION
  Windows version of the pair (see fetch-tools.sh for macOS/Linux). The build calls this
  automatically; run it by hand to prepare a RID you will cross-publish for.

  Binaries land in RPM.Desktop/NativeLibs/tools/<rid>/ and are gitignored. The versions and the
  SHA-256 of each binary are the ones in scripts/tool-versions.txt. A binary that differs from its
  pinned hash is downloaded again, and a download that differs is not installed. So a version change
  reaches every machine on its next build, and no unpinned binary is put where the build copies from.

  ffmpeg: our own build (scripts/build-ffmpeg.sh), from the GitHub release
    rpm-ffmpeg-<FFMPEG_VERSION>-<RPM_FFMPEG_BUILD> in RPM_TOOLS_REPO. If the plain download fails
    (the repository is private) and the GitHub CLI is installed, it is downloaded with gh instead,
    which needs a sign-in (gh auth login) or GH_TOKEN.
  fpcalc: acoustid/chromaprint official release archives.

.PARAMETER Rid
  win-x64 | osx-arm64 | osx-x64 | linux-x64. Defaults to the host RID.

.PARAMETER Force
  Re-download even if the binaries are already present.
#>
[CmdletBinding()]
param(
  [string] $Rid,
  [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$versionsFile = Join-Path $PSScriptRoot 'tool-versions.txt'
$v = @{}
foreach ($line in Get-Content $versionsFile) {
  if ($line -match '^\s*([A-Z0-9_]+)=(.*)$') { $v[$Matches[1]] = $Matches[2].Trim() }
}
$ffmpegTag = "rpm-ffmpeg-$($v.FFMPEG_VERSION)-$($v.RPM_FFMPEG_BUILD)"
$chromaprintVersion = $v.CHROMAPRINT_VERSION

if (-not $Rid) {
  $Rid = if ([Environment]::Is64BitOperatingSystem) { 'win-x64' } else { 'win-x86' }
}

# fpcalc archive name per RID.
$fpcalcAssets = @{
  'win-x64'   = "chromaprint-fpcalc-$chromaprintVersion-windows-x86_64.zip"
  'osx-arm64' = "chromaprint-fpcalc-$chromaprintVersion-macos-arm64.tar.gz"
  'osx-x64'   = "chromaprint-fpcalc-$chromaprintVersion-macos-x86_64.tar.gz"
  'linux-x64' = "chromaprint-fpcalc-$chromaprintVersion-linux-x86_64.tar.gz"
}

if (-not $fpcalcAssets.ContainsKey($Rid)) {
  throw "Unsupported RID '$Rid'. Known: $($fpcalcAssets.Keys -join ', ')"
}

# The pinned hashes for this RID: FFMPEG_BINARY_SHA256_WIN_X64 and so on.
$ridKey       = $Rid.ToUpperInvariant().Replace('-', '_')
$ffmpegPinKey = "FFMPEG_BINARY_SHA256_$ridKey"
$fpcalcPinKey = "FPCALC_BINARY_SHA256_$ridKey"
foreach ($key in $ffmpegPinKey, $fpcalcPinKey) {
  if (-not $v[$key]) { throw "tool-versions.txt pins no $key, so nothing for $Rid can be checked" }
}
$ffmpegPin = $v[$ffmpegPinKey]
$fpcalcPin = $v[$fpcalcPinKey]

$exeSuffix = if ($Rid -like 'win-*') { '.exe' } else { '' }
$repoRoot  = Split-Path -Parent $PSScriptRoot
$toolsDir  = Join-Path $repoRoot 'RPM.Desktop\NativeLibs\tools'
$outDir    = Join-Path $toolsDir $Rid
$ffmpegOut = Join-Path $outDir "ffmpeg$exeSuffix"
$fpcalcOut = Join-Path $outDir "fpcalc$exeSuffix"

function Get-Sha256([string] $Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }

# True when the file exists and is the pinned binary.
function Test-Pinned([string] $Path, [string] $Pin) { (Test-Path -LiteralPath $Path) -and ((Get-Sha256 $Path) -eq $Pin) }

# Throws, before anything is installed, when a download is not the pinned binary.
function Assert-Pinned([string] $Path, [string] $What, [string] $Pin, [string] $Key) {
  $got = Get-Sha256 $Path
  if ($got -ne $Pin) {
    throw "$What has SHA-256 $got; tool-versions.txt pins $Pin. It was not installed. If tool-versions.txt was just moved to a new build, pin the new binary: $Key=$got"
  }
}

if (-not $Force -and (Test-Pinned $ffmpegOut $ffmpegPin) -and (Test-Pinned $fpcalcOut $fpcalcPin)) {
  Write-Host "tools: $Rid already present, skipping (use -Force to refetch)"
  exit 0
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) ("rpm-tools-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null

# Expand-Archive is zip-only, so the .gz ffmpeg payload goes through GZipStream directly.
function Expand-Gzip([string] $Source, [string] $Destination) {
  $in = [IO.File]::OpenRead($Source)
  try {
    $gz = New-Object IO.Compression.GZipStream($in, [IO.Compression.CompressionMode]::Decompress)
    try {
      $out = [IO.File]::Create($Destination)
      try { $gz.CopyTo($out) } finally { $out.Dispose() }
    } finally { $gz.Dispose() }
  } finally { $in.Dispose() }
}

try {
  # --- ffmpeg -------------------------------------------------------------------------------
  Write-Host "tools: downloading ffmpeg $($v.FFMPEG_VERSION)-rpm$($v.RPM_FFMPEG_BUILD) for $Rid ..."
  $ffmpegUrl = "https://github.com/$($v.RPM_TOOLS_REPO)/releases/download/$ffmpegTag/ffmpeg-$Rid.gz"
  try {
    Invoke-WebRequest -Uri $ffmpegUrl -OutFile (Join-Path $work "ffmpeg-$Rid.gz") -UseBasicParsing
  } catch {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
      throw "could not download $ffmpegUrl. If $($v.RPM_TOOLS_REPO) is private, install the GitHub CLI and run gh auth login"
    }
    & gh release download $ffmpegTag -R $v.RPM_TOOLS_REPO -p "ffmpeg-$Rid.gz" -D $work --clobber
    if ($LASTEXITCODE -ne 0) { throw "gh could not download ffmpeg-$Rid.gz from $ffmpegTag" }
  }
  $ffmpegDl = Join-Path $work "ffmpeg$exeSuffix"
  Expand-Gzip -Source (Join-Path $work "ffmpeg-$Rid.gz") -Destination $ffmpegDl
  Assert-Pinned $ffmpegDl "ffmpeg-$Rid.gz from $ffmpegTag" $ffmpegPin $ffmpegPinKey
  Move-Item -LiteralPath $ffmpegDl -Destination $ffmpegOut -Force

  # --- fpcalc -------------------------------------------------------------------------------
  $fpcalcAsset = $fpcalcAssets[$Rid]
  $fpcalcUrl   = "https://github.com/acoustid/chromaprint/releases/download/v$chromaprintVersion/$fpcalcAsset"
  $fpcalcDl    = Join-Path $work $fpcalcAsset
  Write-Host "tools: downloading fpcalc $chromaprintVersion for $Rid ..."
  Invoke-WebRequest -Uri $fpcalcUrl -OutFile $fpcalcDl -UseBasicParsing

  $extract = Join-Path $work 'fp'
  New-Item -ItemType Directory -Force -Path $extract | Out-Null
  if ($fpcalcAsset.EndsWith('.zip')) {
    Expand-Archive -Path $fpcalcDl -DestinationPath $extract -Force
  } else {
    # bsdtar ships with Windows 10+ and reads .tar.gz, so a macOS or Linux RID can be prepared here too.
    & tar -xzf $fpcalcDl -C $extract
    if ($LASTEXITCODE -ne 0) { throw "tar failed to extract $fpcalcAsset" }
  }

  $found = Get-ChildItem -Path $extract -Recurse -File |
           Where-Object { $_.Name -eq "fpcalc$exeSuffix" } |
           Select-Object -First 1
  if (-not $found) { throw "fpcalc$exeSuffix not found inside $fpcalcAsset" }
  Assert-Pinned $found.FullName "fpcalc$exeSuffix from $fpcalcAsset" $fpcalcPin $fpcalcPinKey
  Copy-Item $found.FullName $fpcalcOut -Force

  Write-Host "tools: $Rid ready -> $outDir"
} finally {
  Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
