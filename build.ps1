param(
    [switch]$Run
)
# Build NexusQuickAdd.exe with the .NET Framework C# compiler (no SDK required).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src  = Join-Path $root 'src'
$dist = Join-Path $root 'dist'
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fx   = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'

if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

$refs = @(
    (Join-Path $fx 'WPF\PresentationFramework.dll'),
    (Join-Path $fx 'WPF\PresentationCore.dll'),
    (Join-Path $fx 'WPF\WindowsBase.dll'),
    (Join-Path $fx 'WPF\PresentationUI.dll'),
    (Join-Path $fx 'System.Xaml.dll'),
    (Join-Path $fx 'System.Xml.dll'),
    (Join-Path $fx 'System.Drawing.dll'),
    (Join-Path $fx 'System.Core.dll'),
    (Join-Path $fx 'System.Runtime.Serialization.dll'),
    (Join-Path $fx 'Microsoft.CSharp.dll')
) | Where-Object { Test-Path $_ } | ForEach-Object { '/r:' + $_ }

$files = Get-ChildItem (Join-Path $src '*.cs') | ForEach-Object { $_.FullName }
$out   = Join-Path $dist 'NexusQuickAdd.exe'
$args_ = @(
    '/nologo', '/target:winexe', '/codepage:65001', '/platform:anycpu', '/optimize+',
    "/out:$out",
    "/win32manifest:$(Join-Path $src 'app.manifest')",
    "/resource:$(Join-Path $root 'ui\main.xaml'),main.xaml"
)
$ico = Join-Path $root 'assets\app.ico'
if (Test-Path $ico) { $args_ += "/win32icon:$ico" }
$args_ += $refs
$args_ += $files

& $csc @args_
if ($LASTEXITCODE -ne 0) { throw "build failed: $LASTEXITCODE" }
"built -> $out ($([math]::Round((Get-Item $out).Length/1KB,1)) KB)"

if ($Run) { Start-Process $out }
