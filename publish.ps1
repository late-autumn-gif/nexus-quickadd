param(
    [string]$Repo = 'nexus-quickadd',
    [string]$Tag = 'v1.0.0',
    [switch]$SkipRelease
)
# Build, commit, create the GitHub repo, push and publish a release.
# Requires an authenticated GitHub CLI:  gh auth login
#
# 本机走代理时（例如 Clash 127.0.0.1:7897）：
#   git config http.proxy  http://127.0.0.1:7897
#   git config https.proxy http://127.0.0.1:7897
#   $env:HTTPS_PROXY = 'http://127.0.0.1:7897'   # 上传 release 资产走这个
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

$owner = (gh api user --jq .login)
if (-not $owner) { throw 'gh 未登录，请先执行 gh auth login' }
$full = "$owner/$Repo"
"repo: $full"

powershell -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1')

if (-not (Test-Path (Join-Path $root '.git'))) {
    git init | Out-Null
    git branch -M main
}
git add -A
if ((git status --porcelain).Length -gt 0) { git commit -m "release: $Tag" | Out-Null }

$exists = gh repo view $full --json name 2>$null
if (-not $exists) {
    gh repo create $full --public --source . --remote origin --push --description "Nexus QuickAdd - search and add programs to the Winstep Nexus dock"
} else {
    if (-not (git remote | Select-String -Quiet 'origin')) { git remote add origin "https://github.com/$full.git" }
    git push -u origin main
}

if (-not $SkipRelease) {
    New-Item -ItemType Directory -Force out | Out-Null
    Copy-Item 'dist\NexusQuickAdd.exe' out\
    Copy-Item 'README.md','LICENSE','CHANGELOG.md' out\
    $zip = Join-Path $root 'NexusQuickAdd-win-x64.zip'
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path 'out\*' -DestinationPath $zip
    if (-not (git tag | Select-String -Quiet $Tag)) { git tag $Tag }
    git push origin $Tag
    gh release create $Tag 'dist\NexusQuickAdd.exe' $zip --title "Nexus 速添 $Tag" --generate-notes
    Remove-Item out -Recurse -Force
}

"done -> https://github.com/$full"
