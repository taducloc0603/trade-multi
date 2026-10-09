<#
.SYNOPSIS
  Automated recheck gate for the PrimeXBT plan (docs/plans/primexbt/MASTER-PLAN.md, section 3).

.DESCRIPTION
  Runs checks A1..A7 for a given phase and prints a PASS/WARN/FAIL/SKIP table that can be pasted
  into MASTER-PLAN section 6. Exit code 0 = no FAIL, 1 = at least one FAIL.

  A1 git status            A2 Release build + warning count     A3 test run vs baseline failed list
  A4 locked tests pass     A5 secret scan (repo + latest log)   A6 forbidden close-all strings (phase >= 3)
  A7 protocol probe vs fixtures (phase >= 4)

  Only -WriteBaseline writes files (tools/baseline-*.txt). Nothing else is modified.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File docs/plans/primexbt/tools/recheck.ps1 -Phase 0 -WriteBaseline
  powershell -ExecutionPolicy Bypass -File docs/plans/primexbt/tools/recheck.ps1 -Phase 3
  powershell -ExecutionPolicy Bypass -File docs/plans/primexbt/tools/recheck.ps1 -Phase 3 -Exit
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][int]$Phase,
    [switch]$Exit,
    [switch]$WriteBaseline,
    [switch]$SkipBuild,
    [string]$ProbeCommand = 'C:\Users\laptop\source\tranminhman\scan\primexbt\probe.cmd'
)

$ErrorActionPreference = 'Continue'
$toolsDir = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $toolsDir '..\..\..\..')).Path
$planRel = 'docs/plans/primexbt/'
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
    Write-Host ("PrimeXBT recheck - phase {0} - {1} gate - repo {2}" -f $Phase, $mode, $repoRoot) -ForegroundColor Cyan

    # ---------------- A1 git status ----------------
    $head = (& git rev-parse --short HEAD) | Select-Object -First 1
    $porcelain = @(& git status --porcelain)
    $outside = @($porcelain | Where-Object { $_ -and ($_.Substring(3).Replace('\', '/') -notlike "$planRel*") })
    if ($outside.Count -eq 0) {
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
        $recheckOut = Join-Path $env:TEMP 'primexbt-recheck-build\'
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
            'PartialOpenRecoverySlot_CannotBeClaimedByAutoClose')
        if ($Phase -ge 3) { $locked += 'TradeDesktop.Tests.PrimeXbt.' }
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
    $secretRegex = 'eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.|auth-guard=[A-Za-z0-9+/=]{20,}|refresh_token=[A-Za-z0-9._-]{20,}'
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

    # ---------------- A6 forbidden strings ----------------
    if ($Phase -lt 3) {
        Add-Result 'A6' 'SKIP' 'active from phase 3'
    } else {
        # Production code only: tests and fixtures legitimately mention the forbidden routes.
        $csFiles = @($files | Where-Object { ($_ -like 'TradeDesktop.*') -and ($_ -notlike 'TradeDesktop.Tests*') -and ($_ -like '*.cs') })
        $bad = New-Object System.Collections.Generic.List[string]
        foreach ($rel in $csFiles) {
            $full = Join-Path $repoRoot $rel
            if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }
            foreach ($m in (Select-String -LiteralPath $full -Pattern 'positions/close/all', '"positions/close"' -SimpleMatch -ErrorAction SilentlyContinue)) {
                $bad.Add(("{0}:{1}" -f $rel, $m.LineNumber))
            }
        }
        if ($bad.Count -gt 0) { Add-Result 'A6' 'FAIL' ("forbidden close route in: {0}" -f ($bad -join ', ')) }
        else { Add-Result 'A6' 'PASS' 'no positions/close or close/all in TradeDesktop.*' }
    }

    # ---------------- A7 protocol probe ----------------
    if ($Phase -lt 4) {
        Add-Result 'A7' 'SKIP' 'active from phase 4'
    } elseif (-not (Test-Path $ProbeCommand)) {
        Add-Result 'A7' 'FAIL' ("probe not found: {0} (build it in phase 0, step 0.13)" -f $ProbeCommand)
    } else {
        $probeOut = & $ProbeCommand 2>$null
        $probeCode = $LASTEXITCODE
        $tail = ($probeOut | Select-Object -Last 3) -join ' / '
        if ($probeCode -eq 0) { Add-Result 'A7' 'PASS' ("probe OK: {0}" -f $tail) }
        else { Add-Result 'A7' 'FAIL' ("probe exit {0}: {1}" -f $probeCode, $tail) }
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
