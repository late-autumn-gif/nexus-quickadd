param(
    [switch]$Run,
    [switch]$Full,              # also embed the official Nexus installer (offline bundle, ~42 MB)
    [string]$NexusSetup = ''    # path to NexusSetup.exe; defaults to assets\nexus-setup.exe
)
# Build NexusQuickAdd.exe (the tool) and NexusQuickAdd-Setup.exe (the installer)
# with the .NET Framework C# compiler - no SDK required.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src  = Join-Path $root 'src'
$setupSrc = Join-Path $root 'setup'
$dist = Join-Path $root 'dist'
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fx   = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'

if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

$refs = @(
    (Join-Path $fx 'WPF\PresentationFramework.dll'),
    (Join-Path $fx 'WPF\PresentationCore.dll'),
    (Join-Path $fx 'WPF\WindowsBase.dll'),
    (Join-Path $fx 'System.Xaml.dll'),
    (Join-Path $fx 'System.Xml.dll'),
    (Join-Path $fx 'System.Drawing.dll'),
    (Join-Path $fx 'System.Core.dll'),
    (Join-Path $fx 'System.Runtime.Serialization.dll'),
    (Join-Path $fx 'Microsoft.CSharp.dll')
) | Where-Object { Test-Path $_ } | ForEach-Object { '/r:' + $_ }

$ico = Join-Path $root 'assets\app.ico'
$manifest = Join-Path $src 'app.manifest'

# ---- 1. the tool ----
$toolOut = Join-Path $dist 'NexusQuickAdd.exe'
$toolArgs = @(
    '/nologo', '/target:winexe', '/codepage:65001', '/platform:anycpu', '/optimize+',
    "/out:$toolOut",
    "/win32manifest:$manifest",
    "/resource:$(Join-Path $root 'ui\main.xaml'),main.xaml"
)
if (Test-Path $ico) { $toolArgs += "/win32icon:$ico" }
$toolArgs += $refs
$toolArgs += (Get-ChildItem (Join-Path $src '*.cs') | ForEach-Object { $_.FullName })
& $csc @toolArgs
if ($LASTEXITCODE -ne 0) { throw "tool build failed: $LASTEXITCODE" }
"tool  -> $toolOut ($([math]::Round((Get-Item $toolOut).Length/1KB,1)) KB)"

# ---- 2. the installer (embeds the tool, optionally the official Nexus installer) ----
$setupOut = Join-Path $dist 'NexusQuickAdd-Setup.exe'
$setupArgs = @(
    '/nologo', '/target:winexe', '/codepage:65001', '/platform:anycpu', '/optimize+',
    "/out:$setupOut",
    "/win32manifest:$manifest",
    "/resource:$(Join-Path $setupSrc 'ui.xaml'),setup.xaml",
    "/resource:$toolOut,tool.exe"
)
if (Test-Path $ico) { $setupArgs += "/win32icon:$ico" }

if ($Full) {
    if (-not $NexusSetup) { $NexusSetup = Join-Path $root 'assets\nexus-setup.exe' }
    if (-not (Test-Path $NexusSetup)) { throw "Nexus installer not found: $NexusSetup (put it in assets\nexus-setup.exe or pass -NexusSetup)" }
    $setupArgs += "/resource:$NexusSetup,nexus-setup.exe"
}
$setupArgs += $refs
$setupArgs += (Join-Path $setupSrc 'Setup.cs')
& $csc @setupArgs
if ($LASTEXITCODE -ne 0) { throw "setup build failed: $LASTEXITCODE" }
$sizeMb = [math]::Round((Get-Item $setupOut).Length/1MB, 2)
$mode = if ($Full) { 'with bundled Nexus installer' } else { 'downloads Nexus from winstep.net' }
"setup -> $setupOut ($sizeMb MB, $mode)"

if ($Run) { Start-Process $setupOut }
