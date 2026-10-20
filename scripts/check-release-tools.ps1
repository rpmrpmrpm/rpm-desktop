<#
.SYNOPSIS
  Checks that a Windows release package holds ffmpeg and fpcalc, and that both run and print the
  versions scripts/tool-versions.txt pins.

.DESCRIPTION
  The publish already fails unless both binaries are present at their pinned SHA-256
  (PublishRpmTools in RPM.Desktop.csproj). This reads them back out of the package Velopack wrote,
  which is what Setup and every update unpack. A packaging step that drops one, or a signature that
  stops one from starting, then stops the release before anything is uploaded. Without them the app
  cannot decode or fingerprint anything on a PC that has no ffmpeg on PATH.

  The hashes are not checked here: signing writes into each binary, so the package's copies differ
  from the pinned ones by design.

  Run by .github/workflows/release.yml after `vpk pack`.

.PARAMETER Package
  The full .nupkg `vpk pack` wrote.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string] $Package
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$v = @{}
foreach ($line in Get-Content (Join-Path $PSScriptRoot 'tool-versions.txt')) {
  if ($line -match '^\s*([A-Z0-9_]+)=(.*)$') { $v[$Matches[1]] = $Matches[2].Trim() }
}

# The start of each tool's first -version line. The trailing space keeps 8.1.3-rpm1 from matching
# 8.1.3-rpm10.
$want = [ordered]@{
  'ffmpeg.exe' = "ffmpeg version $($v.FFMPEG_VERSION)-rpm$($v.RPM_FFMPEG_BUILD) "
  'fpcalc.exe' = "fpcalc version $($v.CHROMAPRINT_VERSION) "
}

$packagePath = (Resolve-Path -LiteralPath $Package).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ("rpm-release-tools-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($packagePath)
try {
  foreach ($name in $want.Keys) {
    $entries = @($zip.Entries | Where-Object { $_.FullName -match "(^|/)$([regex]::Escape($name))$" })
    if ($entries.Count -eq 0) {
      throw "$name is not in $(Split-Path -Leaf $packagePath). An app without it cannot decode or fingerprint anything."
    }
    if ($entries.Count -gt 1) {
      throw "$name is in $(Split-Path -Leaf $packagePath) $($entries.Count) times: $(($entries | ForEach-Object FullName) -join ', ')"
    }
    $tool = Join-Path $work $name
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], $tool)

    # stdout only: ffmpeg and fpcalc print the version there, and a native command's stderr read
    # through 2>&1 would arrive as error records.
    $lines = @(& $tool -version)
    if ($LASTEXITCODE -ne 0) { throw "$name from the package exited $LASTEXITCODE on -version" }
    $first = if ($lines.Count -gt 0) { "$($lines[0])" } else { '' }
    if (-not $first.StartsWith($want[$name], [StringComparison]::Ordinal)) {
      throw "$name from the package says '$first'; tool-versions.txt pins '$($want[$name])'"
    }
    "$name at $($entries[0].FullName): $first"
  }
} finally {
  $zip.Dispose()
  Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
