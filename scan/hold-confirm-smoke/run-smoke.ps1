# hold-confirm-mode Phase 6: smoke trên laptop DEMO (docs/plans/hold-confirm-mode/MASTER-PLAN.md).
#
# Dùng:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scan\hold-confirm-smoke\run-smoke.ps1 -Step precheck
#   powershell -NoProfile -ExecutionPolicy Bypass -File scan\hold-confirm-smoke\run-smoke.ps1 -Step full
#
# AN TOÀN (CLAUDE.md Rule 0.0):
#   * Chỉ chạy khi terminal MT5 #1/#2 là Connext-Demo 538216/538217 và title có "Demo Account". Kiểm tra lúc bắt
#     đầu VÀ ngay trước mỗi lần bấm Yes ở "Xác nhận chạy Auto". Không xác minh được ⇒ dừng, không Start.
#   * Smoke CHỈ QUAN SÁT SIGNAL: tắt switch Open Gap Buy/Sell trước Start; không tắt được ⇒ dừng, không Start.
#   * Chỉ ghi Supabase dòng hostname = 'laptop-eoj2n95d' (hard-code), chỉ các cột hold_confirm_mode,
#     open_hold_confirm_ticks, close_hold_confirm_ticks, fast_leg. Giá trị gốc lưu ở state.json và được ghi lại ở cuối.
#   * Không đụng terminal #4 (sàn C, tài khoản thật). fast_leg tạm tắt trên DB trong lúc smoke.
[CmdletBinding()]
param(
    [ValidateSet('precheck', 'dryrun', 'full', 'restore-db')]
    [string]$Step = 'precheck',
    [int]$MsMinutes = 15,
    [int]$TicksMinutes = 15,
    [int]$Ticks = 1,
    # Tuỳ chọn: chạy một exe có sẵn (vd bản đã phát hành) thay vì build từ source.
    # File phải tên TradeMulti.exe để nhận diện tiến trình.
    [string]$AppExe = ''
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$HostName = 'laptop-eoj2n95d'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$WorkDir = Join-Path $env:LOCALAPPDATA 'hold-confirm-smoke'
$AppOutDir = Join-Path $WorkDir 'app'
$StatePath = Join-Path $WorkDir 'state.json'
$ResultPath = Join-Path $WorkDir ("result-{0:yyyyMMdd_HHmmss}.txt" -f (Get-Date))
$LogDir = Join-Path ([Environment]::GetFolderPath('Desktop')) 'trade-log'
$TerminalData = Join-Path $env:APPDATA 'MetaQuotes\Terminal'
$Terminals = @(
    @{ Name = 'A'; Exe = 'C:\Program Files\MetaTrader 5 1\terminal64.exe'; Login = '538216'; Ini = (Join-Path $TerminalData '038C9E8FAFF9EA373522ECC6D5159962\fastleg-a-connext.ini') },
    @{ Name = 'B'; Exe = 'C:\Program Files\MetaTrader 5 2\terminal64.exe'; Login = '538217'; Ini = (Join-Path $TerminalData '0CFEFA8410765D70FC53545BFEFB44F4\fastleg-b-connext.ini') }
)
$ForbiddenTerminalExe = 'C:\Program Files\MetaTrader 5 3\terminal64.exe'

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

function Write-Step([string]$text) {
    $line = "[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $text
    # Write-Host (không phải Write-Output): hàm có giá trị trả về gọi Write-Step bên trong.
    Write-Host $line
    Add-Content -Path $ResultPath -Value $line -Encoding UTF8
}

# Ném lỗi (không exit) để khối finally của Invoke-Full luôn khôi phục DB và đóng app.
function Fail([string]$text) {
    Write-Step "FAIL: $text"
    throw "SMOKE_FAIL: $text"
}

# ---------------------------------------------------------------- Supabase (chỉ dòng laptop)

function Get-SupabaseSettings {
    $di = Get-Content -Raw -Path (Join-Path $RepoRoot 'TradeDesktop.Infrastructure\DependencyInjection.cs')
    $url = [regex]::Match($di, 'DefaultSupabaseUrl = "([^"]+)"').Groups[1].Value
    $key = [regex]::Match($di, 'DefaultSupabaseAnonKey = "([^"]+)"').Groups[1].Value
    if (-not $url -or -not $key) { Fail 'Không đọc được Supabase URL/key từ DependencyInjection.cs' }
    return @{ Url = $url; Headers = @{ apikey = $key; Authorization = "Bearer $key"; 'Content-Type' = 'application/json'; Prefer = 'return=representation' } }
}

function Get-LaptopRow([string]$select) {
    $s = Get-SupabaseSettings
    $uri = "$($s.Url)/rest/v1/configs?hostname=eq.$HostName&select=$select"
    $rows = Invoke-RestMethod -Method Get -Uri $uri -Headers $s.Headers
    if (@($rows).Count -ne 1) { Fail "Supabase trả $(@($rows).Count) dòng cho $HostName (cần đúng 1)" }
    return @($rows)[0]
}

function Set-LaptopColumns([hashtable]$values) {
    foreach ($k in $values.Keys) {
        if ($k -notin @('hold_confirm_mode', 'open_hold_confirm_ticks', 'close_hold_confirm_ticks', 'fast_leg')) {
            Fail "Từ chối ghi cột $k (ngoài phạm vi smoke)"
        }
    }

    $s = Get-SupabaseSettings
    $uri = "$($s.Url)/rest/v1/configs?hostname=eq.$HostName"
    $body = $values | ConvertTo-Json -Depth 20 -Compress
    $rows = Invoke-RestMethod -Method Patch -Uri $uri -Headers $s.Headers -Body ([Text.Encoding]::UTF8.GetBytes($body))
    if (@($rows).Count -ne 1) { Fail "PATCH ảnh hưởng $(@($rows).Count) dòng (cần đúng 1)" }
    Write-Step ("DB set {0}" -f (($values.Keys | Where-Object { $_ -ne 'fast_leg' } | ForEach-Object { "$_=$($values[$_])" }) -join ' '))
}

function Save-OriginalState {
    if (Test-Path $StatePath) {
        Write-Step "state.json đã có (giữ bản gốc từ lần chạy trước): $StatePath"
        return
    }

    $row = Get-LaptopRow 'hold_confirm_mode,open_hold_confirm_ticks,close_hold_confirm_ticks,fast_leg'
    $row | ConvertTo-Json -Depth 20 | Set-Content -Path $StatePath -Encoding UTF8
    Write-Step "Đã lưu cấu hình gốc vào $StatePath"
}

function Restore-OriginalState {
    if (-not (Test-Path $StatePath)) { Write-Step 'Không có state.json, bỏ qua restore DB'; return }
    $orig = Get-Content -Raw -Path $StatePath | ConvertFrom-Json
    Set-LaptopColumns @{
        hold_confirm_mode        = $orig.hold_confirm_mode
        open_hold_confirm_ticks  = $orig.open_hold_confirm_ticks
        close_hold_confirm_ticks = $orig.close_hold_confirm_ticks
        fast_leg                 = $orig.fast_leg
    }
    $check = Get-LaptopRow 'hold_confirm_mode,fast_leg'
    if (($check.fast_leg | ConvertTo-Json -Depth 20 -Compress) -ne ($orig.fast_leg | ConvertTo-Json -Depth 20 -Compress)) {
        Fail 'Restore fast_leg không khớp bản gốc — kiểm tra tay state.json'
    }

    Remove-Item -Path $StatePath
    Write-Step "Đã khôi phục DB về cấu hình gốc (hold_confirm_mode=$($check.hold_confirm_mode), fast_leg như cũ)"
}

function Get-FastLegDisabled {
    $orig = Get-Content -Raw -Path $StatePath | ConvertFrom-Json
    if ($null -eq $orig.fast_leg) { return $null }
    $copy = $orig.fast_leg | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $copy.enabled = $false
    return $copy
}

# ---------------------------------------------------------------- Win32 / UIA

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class SmokeWin32 {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    public static List<IntPtr> DialogsOf(uint pid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h)) {
                var cls = new StringBuilder(64); GetClassName(h, cls, 64);
                if (cls.ToString() == "#32770") list.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
    public static string Title(IntPtr h) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }
}
'@

$WM_COMMAND = 0x0111
$IDOK = 1; $IDCANCEL = 2; $IDYES = 6

function Get-AppProcess { Get-Process -Name 'TradeMulti' -ErrorAction SilentlyContinue | Select-Object -First 1 }

# Đóng các hộp thoại thông báo (HWND không hợp lệ, mất kết nối...). Trả danh sách tiêu đề đã đóng.
function Close-InfoDialogs {
    $app = Get-AppProcess
    if (-not $app) { return @() }
    $closed = @()
    foreach ($h in [SmokeWin32]::DialogsOf([uint32]$app.Id)) {
        $title = [SmokeWin32]::Title($h)
        if ($title -eq 'Xác nhận chạy Auto' -or $title -eq 'Xác nhận thoát') { continue }
        [SmokeWin32]::PostMessage($h, $WM_COMMAND, [IntPtr]$IDCANCEL, [IntPtr]::Zero) | Out-Null
        Start-Sleep -Milliseconds 300
        if ([SmokeWin32]::IsWindowVisible($h)) {
            [SmokeWin32]::PostMessage($h, $WM_COMMAND, [IntPtr]$IDOK, [IntPtr]::Zero) | Out-Null
        }
        $closed += $title
    }
    return $closed
}

function Answer-Dialog([string]$title, [int]$id, [int]$timeoutSec = 15) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $app = Get-AppProcess
        if ($app) {
            foreach ($h in [SmokeWin32]::DialogsOf([uint32]$app.Id)) {
                if ([SmokeWin32]::Title($h) -eq $title) {
                    [SmokeWin32]::SendMessage($h, $WM_COMMAND, [IntPtr]$id, [IntPtr]::Zero) | Out-Null
                    Start-Sleep -Milliseconds 500
                    if (-not [SmokeWin32]::IsWindowVisible($h)) { return $true }
                }
            }
        }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

function Get-MainElement {
    $app = Get-AppProcess
    if (-not $app -or $app.MainWindowHandle -eq [IntPtr]::Zero) { return $null }
    return [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
}

function Find-ByName($root, [string]$name, $controlType) {
    $cond = New-Object Windows.Automation.AndCondition(
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name)),
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)))
    return $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-AppButton([string]$name) {
    $null = Close-InfoDialogs
    $root = Get-MainElement
    if (-not $root) { Fail "Không thấy cửa sổ app khi bấm $name" }
    $btn = Find-ByName $root $name ([Windows.Automation.ControlType]::Button)
    if (-not $btn) { Fail "Không tìm thấy nút $name" }
    if (-not $btn.Current.IsEnabled) { Fail "Nút $name đang disabled" }
    $btn.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Step "Bấm $name"
}

# Switch Open Gap Buy/Sell: ToggleButton không có AutomationId ⇒ lấy toggle gần nhất bên phải nhãn, cùng hàng.
function Set-OpenSwitchesOff {
    $root = Get-MainElement
    if (-not $root) { Fail 'Không thấy cửa sổ app khi tắt switch Open' }
    $toggleCond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::IsTogglePatternAvailableProperty, $true)
    $toggles = $root.FindAll([Windows.Automation.TreeScope]::Descendants, $toggleCond)
    foreach ($label in @('Open Gap Buy', 'Open Gap Sell')) {
        $text = Find-ByName $root $label ([Windows.Automation.ControlType]::Text)
        if (-not $text) { Fail "Không thấy nhãn '$label'" }
        $lr = $text.Current.BoundingRectangle
        $best = $null; $bestDx = [double]::MaxValue
        foreach ($t in $toggles) {
            $r = $t.Current.BoundingRectangle
            $dx = $r.Left - $lr.Right
            if ($dx -ge 0 -and [Math]::Abs(($r.Top + $r.Height / 2) - ($lr.Top + $lr.Height / 2)) -lt 12 -and $dx -lt $bestDx) {
                $best = $t; $bestDx = $dx
            }
        }

        if (-not $best) { Fail "Không thấy switch cạnh '$label'" }
        $pattern = $best.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
        if ($pattern.Current.ToggleState -eq [Windows.Automation.ToggleState]::On) { $pattern.Toggle() }
        Start-Sleep -Milliseconds 300
        if ($pattern.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off) { Fail "Không tắt được switch '$label' ⇒ KHÔNG Start" }
        Write-Step "Switch '$label' = Off"
    }
}

# ---------------------------------------------------------------- Guard demo + terminal

function Assert-DemoTerminals {
    $running = Get-Process terminal64 -ErrorAction SilentlyContinue
    if ($running | Where-Object { $_.Path -eq $ForbiddenTerminalExe }) { Fail 'Terminal #3 đang chạy (có thể ghi đè map A/B) ⇒ dừng' }
    foreach ($t in $Terminals) {
        $p = $running | Where-Object { $_.Path -eq $t.Exe } | Select-Object -First 1
        if (-not $p) { Fail "Terminal $($t.Name) không chạy" }
        $title = (Get-Process -Id $p.Id).MainWindowTitle
        if ($title -notmatch [regex]::Escape($t.Login) -or $title -notmatch 'Connext-Demo' -or $title -notmatch 'Demo Account') {
            Fail "Terminal $($t.Name) KHÔNG xác minh được DEMO (title='$title') ⇒ dừng (Rule 0.0)"
        }
    }
    Write-Step 'Guard demo OK: #1=538216 #2=538217 Connext-Demo, "Demo Account"'
}

function Start-DemoTerminals {
    foreach ($t in $Terminals) {
        $p = Get-Process terminal64 -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $t.Exe }
        if (-not $p) {
            if (-not (Test-Path $t.Ini)) { Fail "Thiếu $($t.Ini)" }
            Start-Process -FilePath $t.Exe -ArgumentList "/config:`"$($t.Ini)`""
            Write-Step "Mở terminal $($t.Name) bằng $([IO.Path]::GetFileName($t.Ini))"
        }
    }

    $deadline = (Get-Date).AddSeconds(240)
    while ((Get-Date) -lt $deadline) {
        $ok = $true
        foreach ($t in $Terminals) {
            $p = Get-Process terminal64 -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $t.Exe } | Select-Object -First 1
            if (-not $p -or (Get-Process -Id $p.Id).MainWindowTitle -notmatch 'Demo Account') { $ok = $false }
        }
        if ($ok) { break }
        Start-Sleep -Seconds 5
    }

    Start-Sleep -Seconds 20 # EA DataExporter gắn chart và tạo map.
    Assert-DemoTerminals
}

# ---------------------------------------------------------------- App

function Build-App {
    if ($AppExe) {
        if (-not (Test-Path $AppExe) -or (Split-Path $AppExe -Leaf) -ne 'TradeMulti.exe') {
            Fail "AppExe không hợp lệ (phải tồn tại và tên TradeMulti.exe): $AppExe"
        }
        $script:AppOutDir = Split-Path (Resolve-Path $AppExe).Path -Parent
        $ver = (Get-Item $AppExe).VersionInfo.FileVersion
        Write-Step "Dùng exe có sẵn (không build): $AppExe FileVersion=$ver"
        return (Resolve-Path $AppExe).Path
    }
    $exe = Join-Path $AppOutDir 'TradeMulti.exe'
    $env:DOTNET_ROLL_FORWARD = 'Major'
    Write-Step "Build Release ⇒ $AppOutDir"
    & dotnet build (Join-Path $RepoRoot 'TradeDesktop.App\TradeDesktop.App.csproj') -c Release -o $AppOutDir --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) { Fail 'Build app thất bại' }
    return $exe
}

function Start-App([string]$exe) {
    if (Get-AppProcess) { Fail 'TradeMulti đang chạy sẵn — đóng tay trước khi smoke' }
    Start-Process -FilePath $exe -WorkingDirectory $AppOutDir
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        $null = Close-InfoDialogs
        if (Get-MainElement) { break }
        Start-Sleep -Seconds 2
    }
    if (-not (Get-MainElement)) { Fail 'App không hiện cửa sổ chính' }
    Start-Sleep -Seconds 8
    $closed = Close-InfoDialogs
    if ($closed) { Write-Step "Đã đóng hộp thoại: $($closed -join ', ')" }
    Write-Step 'App đã mở'
}

function Close-App {
    $app = Get-AppProcess
    if (-not $app) { return }
    $null = Close-InfoDialogs
    $app.CloseMainWindow() | Out-Null
    $null = Answer-Dialog 'Xác nhận thoát' $IDYES 15
    if (-not $app.WaitForExit(20000)) { Write-Step 'App chưa thoát sau 20s (để nguyên, không kill)' } else { Write-Step 'App đã đóng' }
}

function Get-LatestSession {
    $f = Get-ChildItem -Path $LogDir -Filter '*-trade-log.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($f) { return $f.Name.Substring(0, 15) }
    return $null
}

function Start-Session([string]$label) {
    Assert-DemoTerminals
    Set-OpenSwitchesOff
    $before = Get-LatestSession
    Invoke-AppButton 'Start'
    if (-not (Answer-Dialog 'Xác nhận chạy Auto' $IDYES 20)) { Fail 'Không trả lời được hộp thoại Xác nhận chạy Auto' }
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and (Get-LatestSession) -eq $before) { Start-Sleep -Seconds 1 }
    $session = Get-LatestSession
    if ($session -eq $before) { Fail "Start $label không tạo phiên log mới" }
    Write-Step "Phiên $label = $session"
    return $session
}

function Wait-Session([string]$label, [int]$minutes, [string]$session) {
    $end = (Get-Date).AddMinutes($minutes)
    $marketChecked = $false
    while ((Get-Date) -lt $end) {
        Start-Sleep -Seconds 10
        $closed = Close-InfoDialogs
        if ($closed) { Write-Step "Đã đóng hộp thoại: $($closed -join ', ')" }
        if (-not (Get-AppProcess)) { Fail "App thoát giữa phiên $label" }
        if (-not $marketChecked -and (Get-Date) -gt $end.AddMinutes(-$minutes).AddSeconds(90)) {
            $marketChecked = $true
            $moves = Get-PriceMoves $session
            Write-Step "Market check sau 90s: A đổi giá $($moves.A) lần, B $($moves.B) lần"
            if ($moves.A -lt 5 -or $moves.B -lt 5) { return $false }
        }
    }
    return $true
}

# Logger của app giữ file đang ghi ⇒ phải mở với FileShare.ReadWrite (File.ReadLines sẽ báo file đang bị dùng).
function Read-SharedLines([string]$path) {
    if (-not (Test-Path $path)) { return @() }
    $fs = New-Object IO.FileStream($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        $reader = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)
        $lines = New-Object 'System.Collections.Generic.List[string]'
        while ($null -ne ($line = $reader.ReadLine())) { $lines.Add($line) }
        return ,$lines.ToArray()
    }
    finally { $fs.Dispose() }
}

function Get-PriceMoves([string]$session) {
    $path = Join-Path $LogDir "$session-gap-tick.log"
    $a = 0; $b = 0; $lastA = ''; $lastB = ''
    if (Test-Path $path) {
        foreach ($line in (Read-SharedLines $path)) {
            $m = [regex]::Match($line, 'a_bid=(\S+) a_ask=(\S+).* b_bid=(\S+) b_ask=(\S+)')
            if (-not $m.Success) { continue }
            $qa = "$($m.Groups[1].Value)/$($m.Groups[2].Value)"; $qb = "$($m.Groups[3].Value)/$($m.Groups[4].Value)"
            if ($lastA -and $qa -ne $lastA) { $a++ }
            if ($lastB -and $qb -ne $lastB) { $b++ }
            $lastA = $qa; $lastB = $qb
        }
    }
    return @{ A = $a; B = $b }
}

function Get-SessionReport([string]$label, [string]$session) {
    $trade = Join-Path $LogDir "$session-trade-log.log"
    $outcome = Join-Path $LogDir "$session-signal-outcome.log"
    $lines = Read-SharedLines $trade
    $count = { param($pattern) @($lines | Where-Object { $_ -match $pattern }).Count }
    $moves = Get-PriceMoves $session
    $report = [ordered]@{
        label                    = $label
        session                  = $session
        price_moves_a            = $moves.A
        price_moves_b            = $moves.B
        config_line              = (@($lines | Where-Object { $_ -match '\[CONFIG\] hold_confirm_mode=' }) | Select-Object -Last 1)
        open_cycle_started       = & $count '\[OPEN_CYCLE\]\[STARTED\]'
        open_cycle_reset         = & $count '\[OPEN_CYCLE\]\[RESET\]'
        open_cycle_completed     = & $count '\[OPEN_CYCLE\]\[COMPLETED\]'
        open_cycle_triggered     = & $count '\[OPEN_CYCLE\]\[TRIGGERED\]'
        lines_time_mode          = & $count 'confirmation_mode=TIME_AND_MIN_SAMPLES'
        lines_ticks_mode         = & $count 'confirmation_mode=TICKS_AND_MIN_SAMPLES'
        lines_hold_ticks         = & $count 'hold_ticks='
        lines_hold_ticks_zero    = & $count 'hold_ticks=0/'
        stalled_tick_cap         = & $count 'Chân chậm chưa đủ tick'
        long_cycle_warn          = & $count 'long_cycle'
        logger_dropped           = & $count 'dropped_important=[1-9]'
        error_lines              = & $count '\]\[ERROR\]'
        db_warn_lines            = & $count '\[DB\]\[WARN\]'
        outcome_signal_lines     = if (Test-Path $outcome) { @((Read-SharedLines $outcome) | Where-Object { $_ -match '\[SIGNAL_OUTCOME\]\[SIGNAL\]' }).Count } else { 0 }
    }
    foreach ($k in $report.Keys) { Write-Step ("REPORT {0} {1}={2}" -f $label, $k, $report[$k]) }
    $errors = @($lines | Where-Object { $_ -match '\]\[ERROR\]' } | Select-Object -First 10)
    foreach ($e in $errors) { Write-Step "REPORT $label error_sample: $e" }
    return $report
}

function Stop-Session {
    $null = Close-InfoDialogs
    Invoke-AppButton 'Stop'
    Start-Sleep -Seconds 3
}

# ---------------------------------------------------------------- Steps

function Invoke-Precheck {
    Write-Step "PRECHECK (chỉ đọc) host=$HostName"
    try { $null = Get-LaptopRow 'hold_confirm_mode' }
    catch { Write-Step 'DB: MIGRATION CHƯA CHẠY (thiếu cột hold_confirm_mode) — chạy docs/HOLD-CONFIRM-MODE-MIGRATION.sql'; return }
    $row = Get-LaptopRow 'hostname,hold_confirm_mode,open_hold_confirm_ticks,close_hold_confirm_ticks,open_pts,open_confirm_gap_pts,close_pts,close_confirm_gap_pts'
    Write-Step ("DB: hold_confirm_mode={0} open_ticks={1} close_ticks={2} open_pts={3} confirm={4} close_pts={5} close_confirm={6}" -f `
        $row.hold_confirm_mode, $row.open_hold_confirm_ticks, $row.close_hold_confirm_ticks, $row.open_pts, $row.open_confirm_gap_pts, $row.close_pts, $row.close_confirm_gap_pts)
    foreach ($t in $Terminals) {
        $p = Get-Process terminal64 -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $t.Exe } | Select-Object -First 1
        Write-Step ("Terminal {0}: {1}" -f $t.Name, $(if ($p) { (Get-Process -Id $p.Id).MainWindowTitle } else { 'không chạy' }))
        if (-not (Test-Path $t.Ini)) { Write-Step "  THIẾU $($t.Ini)" }
    }
    Write-Step ("TradeMulti đang chạy: {0}" -f [bool](Get-AppProcess))
    Write-Step ("state.json còn sót: {0}" -f (Test-Path $StatePath))
}

# Thử luồng UI (terminal demo, app, switch Off, Start/Yes, Stop, đóng app) KHÔNG ghi DB. Dùng lúc market đóng.
function Invoke-DryRun {
    Write-Step "DRYRUN (không ghi DB) host=$HostName"
    $exe = Build-App
    Start-DemoTerminals
    try {
        Start-App $exe
        $session = Start-Session 'dryrun'
        $null = Wait-Session 'dryrun' 2 $session
        Stop-Session
        $null = Get-SessionReport 'dryrun' $session
    }
    finally {
        try { Close-App } catch { Write-Step "Đóng app lỗi: $($_.Exception.Message)" }
        Write-Step "Kết quả: $ResultPath"
    }
    return 0
}

function Invoke-Full {
    Write-Step "FULL smoke host=$HostName ms=${MsMinutes}' ticks=${TicksMinutes}' ticks=$Ticks"
    $null = Get-LaptopRow 'hold_confirm_mode' # migration phải đã chạy
    $exe = Build-App
    Start-DemoTerminals
    Save-OriginalState

    $completed = $false
    try {
        $fastLegOff = Get-FastLegDisabled
        Set-LaptopColumns @{ hold_confirm_mode = 'ms'; open_hold_confirm_ticks = $Ticks; close_hold_confirm_ticks = $Ticks; fast_leg = $fastLegOff }
        Start-App $exe
        $msSession = Start-Session 'ms'
        $marketOpen = Wait-Session 'ms' $MsMinutes $msSession
        Stop-Session
        $msReport = Get-SessionReport 'ms' $msSession
        if (-not $marketOpen) {
            Write-Step 'MARKET_CLOSED: giá A/B chưa chạy ⇒ dừng smoke, khôi phục DB'
            return 3
        }

        Set-LaptopColumns @{ hold_confirm_mode = 'ticks'; open_hold_confirm_ticks = $Ticks; close_hold_confirm_ticks = $Ticks }
        Invoke-AppButton 'Reconnect'
        Start-Sleep -Seconds 8
        $ticksSession = Start-Session 'ticks'
        $null = Wait-Session 'ticks' $TicksMinutes $ticksSession
        Stop-Session
        $ticksReport = Get-SessionReport 'ticks' $ticksSession

        $gates = [ordered]@{
            ms_has_time_mode_only    = ($msReport.lines_time_mode -gt 0 -and $msReport.lines_ticks_mode -eq 0 -and $msReport.lines_hold_ticks -eq 0)
            ticks_config_logged      = ([string]$ticksReport.config_line -match 'hold_confirm_mode=ticks')
            ticks_has_ticks_mode     = ($ticksReport.lines_ticks_mode -gt 0 -and $ticksReport.lines_hold_ticks -gt 0)
            no_long_cycle_warn       = ($msReport.long_cycle_warn -eq 0 -and $ticksReport.long_cycle_warn -eq 0)
            no_logger_drop           = ($msReport.logger_dropped -eq 0 -and $ticksReport.logger_dropped -eq 0)
        }
        foreach ($k in $gates.Keys) { Write-Step ("GATE {0}={1}" -f $k, $gates[$k]) }
        Write-Step ("SUMMARY ms: cycles={0} triggered={1} | ticks: cycles={2} triggered={3} reset_at_hold_ticks_0={4} stalled_cap={5}" -f `
            $msReport.open_cycle_started, $msReport.open_cycle_triggered, $ticksReport.open_cycle_started, $ticksReport.open_cycle_triggered, $ticksReport.lines_hold_ticks_zero, $ticksReport.stalled_tick_cap)
        $completed = -not ($gates.Values -contains $false)
    }
    finally {
        try { Restore-OriginalState } catch { Write-Step "RESTORE DB LỖI: $($_.Exception.Message) — khôi phục tay từ $StatePath" }
        try {
            if (Get-AppProcess) {
                Invoke-AppButton 'Reconnect'
                Start-Sleep -Seconds 5
                Close-App
            }
        } catch { Write-Step "Đóng app lỗi: $($_.Exception.Message)" }
        Write-Step "Kết quả: $ResultPath"
    }

    if ($completed) { Write-Step 'SMOKE PASS'; return 0 }
    Write-Step 'SMOKE FAIL (xem GATE ở trên)'
    return 1
}

try {
    switch ($Step) {
        'precheck' { Invoke-Precheck; exit 0 }
        'restore-db' { Restore-OriginalState; exit 0 }
        'dryrun' { $code = Invoke-DryRun; exit $code }
        'full' { $code = Invoke-Full; exit $code }
    }
}
catch {
    Write-Step "ABORT: $($_.Exception.Message)"
    exit 2
}
