# Loads soft_oal.dll and onnxruntime.dll on a Windows that has no Visual C++ runtime, once alone and
# once with the runtime rpm publishes beside them (the PublishVcRuntime step in
# RPM.Desktop.csproj copies those files). Run by .github/workflows/test-rpm-vcruntime.yml inside the
# Server Core container, with C:\natives holding the DLLs, the four runtime files and one <dll>.imports.txt per DLL.
#
# Windows PowerShell 5.1, because that is what Server Core has.

param([string]$Load)

# One load per process: a runtime DLL loaded by an earlier case would satisfy the next case's imports.
if ($Load) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Loader {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

    // LOAD_WITH_ALTERED_SEARCH_PATH (8): the DLL's imports are looked for in its own folder first,
    // as rpm.exe's are looked for in the folder rpm.exe is in. The error is read in the same call,
    // before PowerShell can make another system call that overwrites it.
    public static int Load(string path) {
        return LoadLibraryExW(path, IntPtr.Zero, 8) == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
    }
}
'@
    $code = [Loader]::Load($Load)
    exit $code
}

$system = Join-Path $env:SystemRoot 'System32'
$runtime = 'msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll'

$installed = @($runtime | Where-Object { Test-Path (Join-Path $system $_) })
if ($installed.Count -gt 0) {
    Write-Host "This Windows has the Visual C++ runtime in $system ($($installed -join ', ')), so it cannot show what a PC without it does."
    exit 1
}
Write-Host "No Visual C++ runtime in $system."
Write-Host ''

# The DLL's own imports that are neither in System32 nor beside it. API set names (api-ms-*,
# ext-ms-*) are resolved by the loader, not found as files, so they are left out.
function Get-NotFound([string]$Dll, [string]$Folder) {
    Get-Content "C:\natives\$Dll.imports.txt" |
        Where-Object { $_ -notmatch '^(api|ext)-ms-' } |
        Where-Object { -not (Test-Path (Join-Path $system $_)) -and -not (Test-Path (Join-Path $Folder $_)) }
}

function Test-Case([string]$Dll, [bool]$WithRuntime, [string[]]$Companions) {
    $label = if ($WithRuntime) { 'with' } else { 'without' }
    $folder = "C:\cases\$Dll-$label"
    New-Item -ItemType Directory -Force $folder | Out-Null
    Copy-Item "C:\natives\$Dll" $folder
    foreach ($c in $Companions) { Copy-Item "C:\natives\$c" $folder }
    if ($WithRuntime) { foreach ($r in $runtime) { Copy-Item "C:\natives\$r" $folder } }

    $process = Start-Process powershell -Wait -PassThru -NoNewWindow `
        -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-Load', "$folder\$Dll"
    $notFound = @(Get-NotFound $Dll $folder)

    $result = if ($process.ExitCode -eq 0) { 'loads' } else { "does not load (error $($process.ExitCode))" }
    $detail = if ($notFound.Count -gt 0) { ". Not found: $($notFound -join ', ')" } else { '' }
    Write-Host "$Dll, $label the runtime beside it: $result$detail"

    [pscustomobject]@{ Code = $process.ExitCode; NotFound = $notFound }
}

$softWithout = Test-Case 'soft_oal.dll' $false @()
$softWith = Test-Case 'soft_oal.dll' $true @()
# Printed, not judged: Server Core may lack onnxruntime.dll's DirectX imports, which fails it either way.
Test-Case 'onnxruntime.dll' $false @('DirectML.dll') | Out-Null
$onnxWith = Test-Case 'onnxruntime.dll' $true @('DirectML.dll')
Write-Host ''

$failures = @()
if ($softWithout.Code -eq 0) { $failures += 'soft_oal.dll loaded without the runtime, so this Windows does not reproduce H5.' }
if ($softWith.Code -ne 0) { $failures += 'soft_oal.dll did not load with the runtime beside it.' }
if ($onnxWith.Code -ne 0) {
    if ($onnxWith.NotFound.Count -gt 0) {
        Write-Host "onnxruntime.dll needs files Server Core does not have ($($onnxWith.NotFound -join ', ')), so its result here says nothing about the runtime. Not counted."
    }
    else {
        $failures += 'onnxruntime.dll did not load with the runtime beside it, and nothing else it imports is missing.'
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" }
    exit 1
}
Write-Host 'PASS: soft_oal.dll needs the runtime and loads with the copy rpm publishes.'
