<#
.SYNOPSIS
  Automated recheck gate for the Fast Leg / exchange C plan (docs/plans/fast-leg/MASTER-PLAN.md, section 3).

.DESCRIPTION
  Runs checks A1..A7 for a given phase and prints a PASS/WARN/FAIL/SKIP table that can be pasted
  into MASTER-PLAN section 6. Exit code 0 = no FAIL, 1 = at least one FAIL.

  A1 git status + branch   A2 Release build + warning count     A3 test run vs baseline failed list
  A4 locked tests pass     A5 secret scan (repo + latest log)   A6 Rule E grep on *FastLeg* files (phase >= 2)
  A7 MMF probe Local\MT_C_Tick + MT_C_Trades heartbeat (phase >= 2)
  A8 python -I tools/test-fast-leg-report.py (phase >= 7)

  Only -WriteBaseline writes files (tools/baseline-*.txt). Nothing else is modified.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File docs/plans/fast-leg/tools/recheck.ps1 -Phase 0 -WriteBaseline
  powershell -ExecutionPolicy Bypass -File docs/plans/fast-leg/tools/recheck.ps1 -Phase 2
  powershell -ExecutionPolicy Bypass -File docs/plans/fast-leg/tools/recheck.ps1 -Phase 2 -Exit
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][int]$Phase,
    [switch]$Exit,
    [switch]$WriteBaseline,
    [switch]$SkipBuild,
    [string]$TickMapName = 'Local\MT_C_Tick',
    [string]$HeartbeatMapName = 'Local\MT_C_Trades',
    [string]$ExpectedSymbol = '',
    [string]$ExpectedBranch = 'feature/exchange-c-fast-leg'
)

$ErrorActionPreference = 'Continue'
$toolsDir = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $toolsDir '..\..\..\..')).Path
$planRel = 'docs/plans/fast-leg/'
$baselineFailedFile = Join-Path $toolsDir 'baseline-failed-tests.txt'
$baselineWarnFile = Join-Path $toolsDir 'baseline-warnings.txt'
$baselineSecretFile = Join-Path $toolsDir 'baseline-secret-hits.txt'

$results = New-Object System.Collections.Generic.List[object]
function Add-Result([string]$id, [string]$status, [string]$detail) {
    $results.Add([pscustomobject]@{ Id = $id; Status = $status; Detail = $detail }) | Out-Null
    $color = switch ($status) { 'PASS' { 'Green' } 'FAIL' { 'Red' } 'WARN' { 'Yellow' } default { 'Gray' } }
    Write-Host ("[{0}] {1,-4} {2}" -f $id, $status, $detail) -ForegroundColor $color
}

function Write-Lines([string]$path, $lines) {
    # UTF-8 without BOM so the first baseline entry compares cleanly.
    [System.IO.File]::WriteAllLines($path, [string[]]@($lines), (New-Object System.Text.UTF8Encoding($false)))
}

function Get-Sha256([string]$text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').Substring(0, 16)
    } finally { $sha.Dispose() }
}

Push-Location $repoRoot
try {
    $mode = if ($Exit) { 'EXIT' } else { 'ENTRY' }
    Write-Host ("Fast Leg recheck - phase {0} - {1} gate - repo {2}" -f $Phase, $mode, $repoRoot) -ForegroundColor Cyan

    # ---------------- A1 git status + branch ----------------
    $head = (& git rev-parse --short HEAD) | Select-Object -First 1
    $branch = (& git rev-parse --abbrev-ref HEAD) | Select-Object -First 1
    $porcelain = @(& git status --porcelain)
    # debug-logs/ existed untracked before this plan started; it is never part of a phase.
    $outside = @($porcelain | Where-Object { $_ -and ($_.Substring(3).Replace('\', '/') -notlike "$planRel*") -and ($_.Substring(3) -notlike 'debug-logs/*') })
    if ($branch -ne $ExpectedBranch) {
        Add-Result 'A1' 'FAIL' ("HEAD {0} on branch '{1}', expected '{2}'" -f $head, $branch, $ExpectedBranch)
    } elseif ($outside.Count -eq 0) {
        Add-Result 'A1' 'PASS' ("HEAD {0}; changes only under {1} ({2} entries)" -f $head, $planRel, $porcelain.Count)
    } else {
        $list = ($outside | Select-Object -First 8) -join '; '
        Add-Result 'A1' 'WARN' ("HEAD {0}; {1} change(s) outside plan folder: {2}" -f $head, $outside.Count, $list)
    }

    # ---------------- A2 build ----------------
    if ($SkipBuild) {
        Add-Result 'A2' 'SKIP' 'build skipped by -SkipBuild'
    } else {
        $sln = Get-ChildItem -Path $repoRoot -Filter *.sln -File | Select-Object -First 1
        $target = if ($sln) { $sln.FullName } else { $repoRoot }
        # --no-incremental: an up-to-date incremental build reports 0 warnings and would corrupt the baseline.
        # OutDir to a temp folder: never touch bin\Release, which the owner may be running for smoke tests
        # (a running TradeMulti.exe locks its DLLs and the copy step fails with MSB3027).
        $recheckOut = Join-Path $env:TEMP 'fast-leg-recheck-build\'
        $buildOut = & dotnet build $target -c Release -nologo --no-incremental "-p:OutDir=$recheckOut" 2>$null
        $buildCode = $LASTEXITCODE
        $warnLine = $buildOut | Where-Object { $_ -match '^\s*(\d+)\s+Warning\(s\)' } | Select-Object -Last 1
        $warnCount = if ($warnLine -and ($warnLine -match '^\s*(\d+)')) { [int]$Matches[1] } else { -1 }
        if ($WriteBaseline -and $buildCode -eq 0) { Write-Lines $baselineWarnFile @("$warnCount") }
        $baseWarn = if (Test-Path $baselineWarnFile) { [int](Get-Content $baselineWarnFile | Select-Object -First 1) } else { $null }
        if ($buildCode -ne 0) {
            Add-Result 'A2' 'FAIL' ("dotnet build exit code {0}" -f $buildCode)
        } elseif ($null -eq $baseWarn) {
            Add-Result 'A2' 'WARN' ("build OK, {0} warning(s); no baseline yet (run -Phase 0 -WriteBaseline)" -f $warnCount)
        } elseif ($warnCount -gt $baseWarn) {
            Add-Result 'A2' 'FAIL' ("warnings {0} > baseline {1}" -f $warnCount, $baseWarn)
        } else {
            Add-Result 'A2' 'PASS' ("build OK, warnings {0} (baseline {1})" -f $warnCount, $baseWarn)
        }
    }

    # ---------------- A3 + A4 tests (single run) ----------------
    $env:DOTNET_ROLL_FORWARD = 'Major'
    $testProj = Join-Path $repoRoot 'TradeDesktop.Tests\TradeDesktop.Tests.csproj'
    $testOut = & dotnet test $testProj --logger 'console;verbosity=normal' -nologo 2>$null
    $passed = New-Object System.Collections.Generic.List[string]
    $failed = New-Object System.Collections.Generic.List[string]
    foreach ($line in $testOut) {
        if ($line -match '^\s+(Passed|Failed)\s+(.+?)\s+\[[^\]]*\]\s*$') {
            if ($Matches[1] -eq 'Passed') { $passed.Add($Matches[2]) } else { $failed.Add($Matches[2]) }
        }
    }
    $totalLine = $testOut | Where-Object { $_ -match 'Total tests:|Failed!|Passed!' } | Select-Object -Last 1
    if ($passed.Count + $failed.Count -eq 0) {
        Add-Result 'A3' 'FAIL' 'no test results parsed (test build failed?)'
    } else {
        $failedSorted = @($failed | Sort-Object -Unique)
        if ($WriteBaseline) { Write-Lines $baselineFailedFile $failedSorted }
        if (-not (Test-Path $baselineFailedFile)) {
            Add-Result 'A3' 'WARN' ("{0} passed / {1} failed; no baseline yet" -f $passed.Count, $failedSorted.Count)
        } else {
            $baseline = @(Get-Content $baselineFailedFile | Where-Object { $_ })
            $newFails = @($failedSorted | Where-Object { $baseline -notcontains $_ })
            $fixed = @($baseline | Where-Object { $failedSorted -notcontains $_ })
            $detail = "{0} passed / {1} failed (baseline {2})" -f $passed.Count, $failedSorted.Count, $baseline.Count
            if ($fixed.Count -gt 0) { $detail += ("; INFO now passing: {0}" -f ($fixed -join ', ')) }
            if ($newFails.Count -gt 0) {
                Add-Result 'A3' 'FAIL' ($detail + "; NEW FAILURES: " + ($newFails -join ', '))
            } else {
                Add-Result 'A3' 'PASS' $detail
            }
        }

        $locked = @('PlatformNormalizationTests', 'PlatformBSwitchGuardTests', 'ManualPairClosePolicyTests',
            'TryClaimSlotClose', 'ManualClaim_DoesNotPreventAutoFromClaimingAnotherPair',
            'PartialOpenRecoverySlot_CannotBeClaimedByAutoClose',
            'TradeDesktop.Tests.PrimeXbt.', 'BPriceOffset', 'TradeDesktop.Tests.TradeLocker.',
            # Signal/log behaviour that the fast leg gate must leave unchanged while fast_leg is disabled.
            'GapTickLineFormatterTests', 'GapStableOpenIntegrationTests', 'GapStableCloseIntegrationTests',
            'DualSideOpenTriggerTests', 'PriorityCloseRuleTests', 'OpenMaxLastGap', 'SignalGapOutcomeTracker',
            'GapCycleLoggingTests')
        # Fast leg tests are created during phase 1, so they are locked from the phase 1 exit gate onwards.
        if ($Phase -ge 2 -or ($Phase -ge 1 -and $Exit)) { $locked += 'TradeDesktop.Tests.FastLeg.' }
        $lockProblems = New-Object System.Collections.Generic.List[string]
        foreach ($pattern in $locked) {
            $p = @($passed | Where-Object { $_ -like "*$pattern*" })
            $f = @($failed | Where-Object { $_ -like "*$pattern*" })
            if ($f.Count -gt 0) { $lockProblems.Add(("{0}: {1} failing" -f $pattern, $f.Count)) }
            elseif ($p.Count -eq 0) { $lockProblems.Add(("{0}: not found" -f $pattern)) }
        }
        if ($lockProblems.Count -gt 0) {
            Add-Result 'A4' 'FAIL' ($lockProblems -join '; ')
        } else {
            Add-Result 'A4' 'PASS' ("{0} locked test group(s) all passing" -f $locked.Count)
        }
    }

    # ---------------- A5 secret scan ----------------
    # TradeLocker adds JSON-style Keycloak tokens ("refresh_token": "...") on top of the PrimeXBT patterns.
    $secretRegex = 'eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.|auth-guard=[A-Za-z0-9+/=]{20,}|refresh_token=[A-Za-z0-9._-]{20,}|"(refresh|access)_token"\s*:\s*"[A-Za-z0-9._-]{20,}'
    $textExt = @('.cs', '.xaml', '.md', '.json', '.sql', '.ps1', '.py', '.txt', '.config', '.yml', '.yaml', '.csproj', '.props', '.cmd', '.bat', '.ts', '.js')
    $files = @(& git ls-files) + @(& git ls-files -o --exclude-standard)
    $files = @($files | Where-Object { $_ -and ($textExt -contains [System.IO.Path]::GetExtension($_).ToLowerInvariant()) } | Sort-Object -Unique)
    $hits = New-Object System.Collections.Generic.List[string]
    foreach ($rel in $files) {
        $full = Join-Path $repoRoot $rel
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }
        foreach ($m in (Select-String -LiteralPath $full -Pattern $secretRegex -AllMatches -ErrorAction SilentlyContinue)) {
            foreach ($mm in $m.Matches) { $hits.Add(("{0}|{1}" -f $rel.Replace('\', '/'), (Get-Sha256 $mm.Value))) }
        }
    }
    $logDir = Join-Path ([Environment]::GetFolderPath('Desktop')) 'trade-log'
    $logNote = 'no trade-log dir'
    if (Test-Path $logDir) {
        $all = @(Get-ChildItem -Path $logDir -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
        # Session files share the prefix yyyyMMdd_HHmmss (4 files per Start); monitor logs are {date}-ctrader/-primexbt.log.
        $newestSession = $all | Where-Object { $_.Name -match '^\d{8}_\d{6}' } | Select-Object -First 1
        $sessionFiles = @()
        $prefix = '-'
        if ($newestSession) {
            $prefix = $newestSession.Name.Substring(0, 15)
            $sessionFiles += @($all | Where-Object { $_.Name.StartsWith($prefix) })
        }
        $sessionFiles += @($all | Where-Object { $_.Name -like '*-ctrader.log' } | Select-Object -First 1)
        $sessionFiles += @($all | Where-Object { $_.Name -like '*-primexbt.log' } | Select-Object -First 1)
        $sessionFiles += @($all | Where-Object { $_.Name -like '*-tradelocker.log' } | Select-Object -First 1)
        foreach ($lf in $sessionFiles) {
            foreach ($m in (Select-String -LiteralPath $lf.FullName -Pattern $secretRegex -AllMatches -ErrorAction SilentlyContinue)) {
                foreach ($mm in $m.Matches) { $hits.Add(("LOG:{0}|{1}" -f $lf.Name, (Get-Sha256 $mm.Value))) }
            }
        }
        $logNote = ("logs: session {0} + monitor ({1} file(s))" -f $prefix, $sessionFiles.Count)
    }
    $repoHits = @($hits | Where-Object { $_ -notlike 'LOG:*' } | Sort-Object -Unique)
    if ($WriteBaseline) { Write-Lines $baselineSecretFile $repoHits }
    $allowed = if (Test-Path $baselineSecretFile) { @(Get-Content $baselineSecretFile | Where-Object { $_ }) } else { @() }
    $newHits = @($hits | Sort-Object -Unique | Where-Object { $allowed -notcontains $_ })
    if ($newHits.Count -gt 0) {
        $where = ($newHits | ForEach-Object { $_.Split('|')[0] } | Sort-Object -Unique) -join ', '
        Add-Result 'A5' 'FAIL' ("{0} new secret-like match(es) in: {1}" -f $newHits.Count, $where)
    } else {
        Add-Result 'A5' 'PASS' ("{0} text file(s) + {1}; {2} baseline-allowed match(es)" -f $files.Count, $logNote, $allowed.Count)
    }

    # ---------------- A6 Rule E grep ----------------
    if ($Phase -lt 2) {
        Add-Result 'A6' 'SKIP' 'active from phase 2'
    } else {
        # Fast leg may only block or defer; it must never reach an order path (CLAUDE.md Rule E). Engine changes are
        # covered by the locked tests in A4, this grep covers the new FastLeg* production files.
        $fastLegFiles = @($files | Where-Object { ($_ -like 'TradeDesktop.*') -and ($_ -notlike 'TradeDesktop.Tests*') -and ($_ -like '*.cs') -and ([System.IO.Path]::GetFileName($_) -like '*FastLeg*') })
        $bad = New-Object System.Collections.Generic.List[string]
        foreach ($rel in $fastLegFiles) {
            $full = Join-Path $repoRoot $rel
            if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }
            foreach ($m in (Select-String -LiteralPath $full -Pattern 'OpenPairAsync', 'ClosePairAsync', 'SendOrderAsync', 'SendMarketOrderAsync', 'TryMarkCloseTriggered', 'AllocatePendingOpenSlot', 'ManualClosePairBySlotAsync' -SimpleMatch -ErrorAction SilentlyContinue)) {
                $bad.Add(("{0}:{1}" -f $rel, $m.LineNumber))
            }
        }
        if ($fastLegFiles.Count -eq 0) { Add-Result 'A6' 'WARN' 'no *FastLeg* production file found' }
        elseif ($bad.Count -gt 0) { Add-Result 'A6' 'FAIL' ("order path referenced from fast leg code: {0}" -f ($bad -join ', ')) }
        else { Add-Result 'A6' 'PASS' ("{0} FastLeg file(s), no order path referenced" -f $fastLegFiles.Count) }
    }

    # ---------------- A7 MMF probe ----------------
    if ($Phase -lt 2) {
        Add-Result 'A7' 'SKIP' 'active from phase 2'
    } else {
        # PowerShell 5.1 runs on .NET Framework: no Environment.TickCount64, and the 32-bit TickCount wraps after
        # 24.9 days. Use the same GetTickCount64 clock that the EA stamps into the header.
        if (-not ('FastLegProbeNative' -as [type])) {
            Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public static class FastLegProbeNative { [DllImport("kernel32.dll")] public static extern ulong GetTickCount64(); }'
        }
        Add-Type -AssemblyName System.Core
        $now = [FastLegProbeNative]::GetTickCount64()
        $tick = $null
        try {
            $mmf = [System.IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($TickMapName, [System.IO.MemoryMappedFiles.MemoryMappedFileRights]::Read)
            try {
                # 16 B header + 48 B body: the symbol occupies bytes 48..63.
                $acc = $mmf.CreateViewAccessor(0, 64, [System.IO.MemoryMappedFiles.MemoryMappedFileAccess]::Read)
                try {
                    $symBytes = New-Object byte[] 16
                    [void]$acc.ReadArray(48, $symBytes, 0, 16)
                    $tick = [pscustomobject]@{
                        Version = $acc.ReadInt32(0); Ts = $acc.ReadUInt64(4)
                        Bid = $acc.ReadDouble(16); Ask = $acc.ReadDouble(24); Spread = $acc.ReadDouble(32)
                        Symbol = ([System.Text.Encoding]::ASCII.GetString($symBytes)).TrimEnd([char]0).Trim()
                    }
                } finally { $acc.Dispose() }
            } finally { $mmf.Dispose() }
        } catch {
            $tick = $null
        }
        if ($null -eq $tick) {
            Add-Result 'A7' 'SKIP' ("map {0} not found (terminal C not running in this Windows session?)" -f $TickMapName)
        } else {
            $hb = $null
            try {
                $hmmf = [System.IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($HeartbeatMapName, [System.IO.MemoryMappedFiles.MemoryMappedFileRights]::Read)
                try {
                    $hacc = $hmmf.CreateViewAccessor(0, 16, [System.IO.MemoryMappedFiles.MemoryMappedFileAccess]::Read)
                    try { $hb = [pscustomobject]@{ Ts = $hacc.ReadUInt64(4); Connected = $hacc.ReadInt32(12) } } finally { $hacc.Dispose() }
                } finally { $hmmf.Dispose() }
            } catch { $hb = $null }
            $tickAge = if ($now -ge $tick.Ts) { [long]($now - $tick.Ts) } else { 0 }
            $hbText = 'hb=n/a'
            $problems = New-Object System.Collections.Generic.List[string]
            $warnings = New-Object System.Collections.Generic.List[string]
            if ($tick.Version -ne 1) { $problems.Add(("version={0}" -f $tick.Version)) }
            if ($ExpectedSymbol -and ($tick.Symbol -ne $ExpectedSymbol)) { $problems.Add(("symbol '{0}' != '{1}'" -f $tick.Symbol, $ExpectedSymbol)) }
            if ($hb) {
                $hbAge = if ($now -ge $hb.Ts) { [long]($now - $hb.Ts) } else { 0 }
                $hbText = ("hb_age={0}ms connected={1}" -f $hbAge, $hb.Connected)
                if ($hbAge -gt 5000) { $problems.Add(("heartbeat age {0} ms > 5000" -f $hbAge)) }
                if ($hb.Connected -eq 0) { $problems.Add('terminal C not connected to broker') }
            } else {
                $warnings.Add(("heartbeat map {0} not found" -f $HeartbeatMapName))
            }
            # The EA only writes the tick map in OnTick: an old tick is normal on weekends or in a very quiet market.
            if ($tickAge -gt 5000) { $warnings.Add(("tick age {0} ms" -f $tickAge)) }
            if ([Math]::Abs(($tick.Ask - $tick.Bid) - $tick.Spread) -gt 1e-9) { $warnings.Add('spread != ask-bid (torn read during probe?)') }
            $detail = ("{0} sym={1} bid={2} ask={3} tick_age={4}ms {5}" -f $TickMapName, $tick.Symbol, $tick.Bid, $tick.Ask, $tickAge, $hbText)
            if ($problems.Count -gt 0) { Add-Result 'A7' 'FAIL' ($detail + '; ' + ($problems -join '; ')) }
            elseif ($warnings.Count -gt 0) { Add-Result 'A7' 'WARN' ($detail + '; ' + ($warnings -join '; ')) }
            else { Add-Result 'A7' 'PASS' $detail }
        }
    }

    # ---------------- A8 Phase 7 report tool tests ----------------
    if ($Phase -lt 7) {
        Add-Result 'A8' 'SKIP' 'active from phase 7'
    } else {
        $python = Get-Command python -ErrorAction SilentlyContinue
        if (-not $python) {
            Add-Result 'A8' 'FAIL' 'python not found (needed for tools/test-fast-leg-report.py)'
        } else {
            $pyOut = & $python.Source -I (Join-Path $toolsDir 'test-fast-leg-report.py') 2>&1 | Out-String
            $ranLine = ($pyOut -split "`r?`n" | Where-Object { $_ -match '^Ran \d+ tests' } | Select-Object -Last 1)
            if ($LASTEXITCODE -eq 0) { Add-Result 'A8' 'PASS' ("test-fast-leg-report.py: {0}" -f $ranLine) }
            else { Add-Result 'A8' 'FAIL' ("test-fast-leg-report.py exit {0}: {1}" -f $LASTEXITCODE, $ranLine) }
        }
    }

    # ---------------- summary ----------------
    $failCount = @($results | Where-Object { $_.Status -eq 'FAIL' }).Count
    $verdict = if ($failCount -eq 0) { 'PASS' } else { 'FAIL' }
    $auto = ($results | ForEach-Object { "{0}={1}" -f $_.Id, $_.Status }) -join ' '
    Write-Host ''
    Write-Host ("Verdict: {0}  ({1})" -f $verdict, $auto) -ForegroundColor $(if ($failCount -eq 0) { 'Green' } else { 'Red' })
    Write-Host 'Row for MASTER-PLAN section 6 (fill manual checks T*):'
    Write-Host ("| {0} | {1} | {2} | {3} | {4} | auto {5}: {6} / tay: ... | ... |" -f (Get-Date -Format 'yyyy-MM-dd'), $Phase,
        $(if ($Exit) { '7. Recheck ra' } else { '1. Recheck vao' }), $head, $(if ($totalLine) { $totalLine.Trim() } else { '-' }), $verdict, $auto)
    if ($WriteBaseline) { Write-Host ("Baseline written to {0}" -f $toolsDir) -ForegroundColor Cyan }

    if ($failCount -gt 0) { exit 1 } else { exit 0 }
}
finally {
    Pop-Location
}
