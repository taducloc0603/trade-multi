<#
.SYNOPSIS
    Cai dat + kiem tra moi truong VPS truoc khi chay nghiem thu Phase 7.

.DESCRIPTION
    Chi lam nhung viec an toan va dao nguoc duoc (timezone, tat ngu). Phan con lai chi KIEM va bao cao.
    KHONG tu tai build, KHONG sua DB, KHONG tu chay app.

    Chay duoc nhieu lan, ket qua nhu nhau.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File vps-setup.ps1 -AppDir "C:\TradeMulti"
#>
[CmdletBinding()]
param(
    [string]$AppDir = "C:\TradeMulti",
    [string]$TickMapName = "Local\MT_A_Tick",
    [switch]$SkipConfigure
)

$ErrorActionPreference = 'Continue'
$results = [ordered]@{}

function Show-Section([string]$title) {
    Write-Host ""
    Write-Host "=== $title ===" -ForegroundColor Cyan
}

function Set-Result([string]$key, [bool]$ok, [string]$detail) {
    $results[$key] = [pscustomobject]@{ Ok = $ok; Detail = $detail }
    $mark = if ($ok) { "DAT  " } else { "THIEU" }
    $color = if ($ok) { 'Green' } else { 'Yellow' }
    Write-Host ("  [{0}] {1}: {2}" -f $mark, $key, $detail) -ForegroundColor $color
}

# --- 1. Thong tin may -------------------------------------------------------
Show-Section "1. Thong tin may"
$hostNameLower = $env:COMPUTERNAME.ToLower()
Write-Host "  hostname (dung lam khoa config trong DB): " -NoNewline
Write-Host $hostNameLower -ForegroundColor Magenta
$os = Get-CimInstance Win32_OperatingSystem
$cs = Get-CimInstance Win32_ComputerSystem
Write-Host ("  Windows : {0}" -f $os.Caption)
Write-Host ("  CPU/RAM : {0} nhan / {1:N1} GB" -f $cs.NumberOfLogicalProcessors, ($cs.TotalPhysicalMemory / 1GB))
Write-Host ("  Timezone: {0}" -f (Get-TimeZone).Id)
Write-Host ("  Gio may : {0}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))

# --- 2. Cau hinh ------------------------------------------------------------
Show-Section "2. Cau hinh Windows"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($SkipConfigure) {
    Write-Host "  (bo qua theo -SkipConfigure)" -ForegroundColor DarkGray
}
elseif (-not $isAdmin) {
    Write-Host "  KHONG co quyen admin - bo qua phan cau hinh." -ForegroundColor Yellow
    Write-Host "  Mo PowerShell bang 'Run as administrator' roi chay lai de dat timezone va tat ngu."
}
else {
    # UTC+7: moi moc gio trong config va log la gio LOCAL (start_time_hold, gio nghi san 03:59:45-05:00).
    # VPS mac dinh thuong la UTC => lech 7 tieng, cau hinh gio sai hoan toan.
    $tzTarget = 'SE Asia Standard Time'
    if ((Get-TimeZone).Id -ne $tzTarget) {
        Set-TimeZone -Id $tzTarget
        Write-Host "  Da dat timezone -> $tzTarget (UTC+7)" -ForegroundColor Green
    } else {
        Write-Host "  Timezone da dung ($tzTarget)" -ForegroundColor Green
    }

    powercfg /change standby-timeout-ac 0 | Out-Null
    powercfg /change monitor-timeout-ac 0 | Out-Null
    powercfg /change hibernate-timeout-ac 0 | Out-Null
    Write-Host "  Da tat sleep/hibernate khi cam dien" -ForegroundColor Green
}
Set-Result 'Timezone UTC+7' ((Get-TimeZone).Id -eq 'SE Asia Standard Time') (Get-TimeZone).Id

# --- 3. Ket noi ra san ------------------------------------------------------
Show-Section "3. Ket noi FIX toi cTrader"
foreach ($port in 5211, 5212) {
    $label = if ($port -eq 5211) { 'QUOTE' } else { 'TRADE' }
    $samples = @()
    foreach ($i in 1..3) {
        $client = New-Object Net.Sockets.TcpClient
        $sw = [Diagnostics.Stopwatch]::StartNew()
        try { $client.Connect('live.cfixapi.com', $port); $sw.Stop(); $samples += $sw.ElapsedMilliseconds }
        catch { $sw.Stop() }
        finally { $client.Close() }
    }
    if ($samples.Count -gt 0) {
        $avg = [math]::Round(($samples | Measure-Object -Average).Average)
        Set-Result "FIX $label ($port)" $true ("bat tay TCP ~{0} ms (mau: {1})" -f $avg, ($samples -join ', '))
    } else {
        Set-Result "FIX $label ($port)" $false "KHONG ket noi duoc - kiem firewall/outbound"
    }
}

# --- 4. Bo cai app ----------------------------------------------------------
Show-Section "4. Bo cai app tai $AppDir"
$required = @{
    'TradeMulti.exe'      = 'khong co app'
    'FIX44-CSERVER.xml'   = 'SAN B CHET CAM - dictionary resolve bang AppContext.BaseDirectory'
    'mt5engine_capi.dll'  = 'chan A khong click duoc lenh nao'
}
foreach ($file in $required.Keys) {
    $path = Join-Path $AppDir $file
    Set-Result $file (Test-Path $path) $(if (Test-Path $path) { $path } else { "THIEU -> " + $required[$file] })
}
$exe = Join-Path $AppDir 'TradeMulti.exe'
if (Test-Path $exe) {
    $ver = (Get-Item $exe).VersionInfo
    Write-Host ("  Version : {0}" -f $ver.ProductVersion)
}

# --- 5. MT5 chan A da ghi shared memory chua --------------------------------
Show-Section "5. Shared memory chan A (MT5 + EA)"
$maps = @($TickMapName, ($TickMapName -replace '_Tick$', '_Trades'), ($TickMapName -replace '_Tick$', '_History'))
foreach ($map in $maps) {
    try {
        $mmf = [System.IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($map)
        $mmf.Dispose()
        Set-Result $map $true "doc duoc"
    } catch {
        Set-Result $map $false "khong ton tai - MT5 chua chay hoac EA chua gan"
    }
}
$mt = Get-Process terminal64, terminal -ErrorAction SilentlyContinue
Write-Host ("  Tien trinh MT dang chay: {0}" -f $(if ($mt) { ($mt | ForEach-Object { "$($_.ProcessName)($($_.Id))" }) -join ', ' } else { 'khong co' }))

# --- 6. Tong ket ------------------------------------------------------------
Show-Section "6. Tong ket"
$missing = $results.Keys | Where-Object { -not $results[$_].Ok }
if ($missing) {
    Write-Host "  CHUA XONG:" -ForegroundColor Yellow
    $missing | ForEach-Object { Write-Host ("    - {0}: {1}" -f $_, $results[$_].Detail) }
} else {
    Write-Host "  Tat ca muc tu dong kiem duoc deu DAT." -ForegroundColor Green
}

Write-Host ""
Write-Host "  Bon viec script KHONG tu lam duoc, phai lam tay:" -ForegroundColor Cyan
Write-Host "    1. Tao row config trong DB voi hostname = '$hostNameLower' (copy tu row cu, doi hostname)"
Write-Host "    2. Dat max_total_opens = 1 trong row do (dang la 3)"
Write-Host "    3. Chup lai HWND tren chinh VPS nay - handle cua may cu vo nghia (Rule G: cN -> tN)"
Write-Host "    4. TAT app o may cu truoc khi chay - hai phien FIX cung SenderCompID se da nhau"
Write-Host ""
Write-Host "  Sau khi bam Start, kiem hai dong trong log phien:" -ForegroundColor Cyan
Write-Host "    [BUILD] version=...        -> dung ban build nao"
Write-Host "    [HEDGE_VOLUME][INFO] ...   -> khoi luong hai chan can. WARN/ERROR thi DUNG LAI."
