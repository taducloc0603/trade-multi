param(
  [string]$Version = "1.0.1"
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repo = "taducloc0603/trade-multi"
$appName = "TradeMulti"

$tag = "v$Version"
$zip = "$appName-$Version-portable-win-x64.zip"
$url = "https://github.com/$repo/releases/download/$tag/$zip"

$base = "C:\TradeMulti"
$app  = Join-Path $base "app"
$pkg  = Join-Path $base $zip
$bak  = Join-Path $base ("backup-" + (Get-Date -Format "yyyyMMdd-HHmmss"))

Write-Host "=== Update TradeMulti $Version ===" -ForegroundColor Cyan

# 1) Stop app if running
Get-Process $appName -ErrorAction SilentlyContinue | Stop-Process -Force

# 2) Prepare folder
New-Item -ItemType Directory -Path $base -Force | Out-Null

# 3) Backup old app
if (Test-Path $app) {
  New-Item -ItemType Directory -Path $bak -Force | Out-Null
  Copy-Item "$app\*" $bak -Recurse -Force
  Write-Host "Backup created: $bak" -ForegroundColor Yellow
}

# 4) Download release zip
Write-Host "Downloading: $url"
Invoke-WebRequest -Uri $url -OutFile $pkg

# 5) Replace app folder
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
Expand-Archive -Path $pkg -DestinationPath $app -Force

# 6) Validate required files
$exePath = Join-Path $app "$appName.exe"
$dllPath = Join-Path $app "mt5engine_capi.dll"

$exeOk = Test-Path $exePath
$dllOk = Test-Path $dllPath

if (-not $exeOk -or -not $dllOk) {
  throw "Missing required files after extract. exe=$exeOk, mt5engine_capi.dll=$dllOk"
}

# 7) Create desktop shortcut
$desktop = [Environment]::GetFolderPath("Desktop")
$shortcutPath = Join-Path $desktop "TradeMulti.lnk"

$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut($shortcutPath)
$sc.TargetPath = $exePath
$sc.WorkingDirectory = $app
$sc.IconLocation = $exePath
$sc.Save()

Write-Host "Shortcut created: $shortcutPath" -ForegroundColor Yellow

# 8) Start app
Start-Process $exePath

Write-Host "DONE. App started from: $app" -ForegroundColor Green
