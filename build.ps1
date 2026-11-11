param(
    [switch]$Tests,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $PSScriptRoot

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path -LiteralPath $csc)) {
    throw "Could not find the .NET Framework C# compiler (csc.exe)."
}

$references = @(
    "System.dll",
    "System.Core.dll",
    "System.Windows.Forms.dll",
    "System.Drawing.dll",
    "System.Net.Http.dll",
    "System.Runtime.Serialization.dll"
)

$core = @(
    "JsonStore.cs",
    "AppSettings.cs",
    "HttpUtil.cs",
    "LinkExtractor.cs",
    "DownloadTask.cs",
    "DownloadManager.cs"
)

$appSources = $core + @(
    "Theme.cs",
    "MainForm.cs",
    "MainForm.Actions.cs",
    "MainForm.Queue.cs",
    "Program.cs"
)

if ($Clean) {
    Remove-Item -LiteralPath "LinkDownloader.exe" -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath "LinkDownloaderTests.exe" -ErrorAction SilentlyContinue
}

$common = @(
    "/nologo",
    "/codepage:65001",
    "/optimize+",
    "/platform:anycpu"
) + ($references | ForEach-Object { "/r:$_" })

$appArgs = $common + @(
    "/target:winexe",
    "/win32manifest:app.manifest",
    "/out:LinkDownloader.exe"
) + $appSources

& $csc $appArgs
if ($LASTEXITCODE -ne 0) {
    throw "C# build failed with exit code $LASTEXITCODE."
}
Write-Host "Built LinkDownloader.exe"

if ($Tests) {
    $testSources = $core + @("Tests.cs")
    $testArgs = $common + @(
        "/target:exe",
        "/out:LinkDownloaderTests.exe"
    ) + $testSources

    & $csc $testArgs
    if ($LASTEXITCODE -ne 0) {
        throw "C# test build failed with exit code $LASTEXITCODE."
    }
    Write-Host "Built LinkDownloaderTests.exe"
    & ".\LinkDownloaderTests.exe"
    exit $LASTEXITCODE
}
