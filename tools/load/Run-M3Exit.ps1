<#
.SYNOPSIS
    The M3 exit criterion as one command (spec 12.2 "a crafted item can be made from gathered materials and used";
    docs/plans/m3-jobs-crafting-plan.md section 1; WP-M3-12, spec 11.5).

.DESCRIPTION
    Runs tools/load/Run-BotLoad.ps1 with the m3-exit scenario: docker compose (PostgreSQL, Redis), Debug builds, migration,
    then `BotClient loadtest --scenario m3-exit`: two fresh characters, seeded to level 16 (DbMigrate --set-level, a test
    seed), play one script each (BotBehaviour loopOnce): the Blacksmith (pick the job, earn gold, buy a pickaxe, gather,
    refine, craft a sword, equip it, fight with it, one enchant attempt, buy and drink a potion in combat) and the Jeweler
    (pick the job, refine a rough gem, socket it). Prints every script entry as done, not yet (with the reason: the
    feature or its content has not landed) or failed, then the M3 counters of the economy report (job XP per activity,
    items created and destroyed by M3 reason) from the run's telemetry. Results in artifacts/load/<run-id>/summary.json.
    Exit code 0 only when every entry is done and every other criterion holds.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/load/Run-M3Exit.ps1 -DbSuffix wpm312
    powershell -ExecutionPolicy Bypass -File tools/load/Run-M3Exit.ps1 -DbSuffix wpm312 -NoDocker -Link rtt100
#>
param(
    # A BotLinkProfile row instead of the scenario's (lan): rtt100, rtt200, rtt200-loss2.
    [string]$Link = '',
    # Own dev databases mmorpg_global_<suffix> and mmorpg_realm_1_<suffix> (DbMigrate --db-suffix).
    [ValidatePattern('^[a-z0-9_]{0,32}$')]
    [string]$DbSuffix = '',
    [string]$RunId = '',
    # The time limit instead of the scenario's 30 minutes (the run ends earlier once both scripts finished).
    [int]$DurationMs = 0,
    # Skip docker compose (the services are already up).
    [switch]$NoDocker
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ($RunId.Length -eq 0) {
    $RunId = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd-HHmmss') + '-m3-exit'
    if ($Link.Length -gt 0) { $RunId += '-' + $Link }
}
$runDir = Join-Path $repo (Join-Path 'artifacts\load' $RunId)

# Run-BotLoad.ps1 ends with `exit`, so it runs in a child PowerShell; its output streams through.
$arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'Run-BotLoad.ps1'),
    '-Scenario', 'm3-exit', '-RunId', $RunId)
if ($Link.Length -gt 0) { $arguments += @('-Link', $Link) }
if ($DbSuffix.Length -gt 0) { $arguments += @('-DbSuffix', $DbSuffix) }
if ($DurationMs -gt 0) { $arguments += @('-DurationMs', "$DurationMs") }
if ($NoDocker) { $arguments += '-NoDocker' }
& powershell @arguments
$code = $LASTEXITCODE

$summaryPath = Join-Path $runDir 'summary.json'
if (-not (Test-Path $summaryPath)) {
    Write-Host "m3-exit: no summary.json in $runDir (the run did not get to the bots)"
    exit $(if ($code -ne 0) { $code } else { 1 })
}
$summary = Get-Content $summaryPath -Raw | ConvertFrom-Json
Write-Host '== m3-exit: the exit criterion per script entry'
foreach ($bot in $summary.botsDetail) {
    Write-Host ("bot{0} {1} ({2} {3}, level {4}):" -f $bot.index, $bot.behaviour, $bot.classId, $bot.character, $bot.level)
    if ($null -eq $bot.script) { Write-Host '  no script (not a loopOnce behaviour)'; continue }
    foreach ($step in $bot.script) {
        $line = "  [{0}] {1}" -f $step.outcome, $step.entry
        if ($step.reason.Length -gt 0) { $line += ': ' + $step.reason }
        Write-Host $line
    }
}
$s = $summary.scripts
Write-Host ("entries {0}: done {1}, not yet {2}, failed {3}, timed out {4}, not played {5}" -f `
    $s.entries, $s.done, $s.notYet, $s.failed, $s.timedOut, $s.pending)
$criterion = $summary.criteria | Where-Object { $_.name -eq 'every script step done' } | Select-Object -First 1
if ($null -ne $criterion) {
    $verdict = if ($criterion.pass) { 'PASS' } else { 'FAIL' }
    Write-Host ("[{0}] {1}: {2}" -f $verdict, $criterion.name, $criterion.actual)
}
if ($null -ne $summary.ticks) {
    $t = $summary.ticks
    Write-Host ("tick p99 {0:0.00} ms of {1:0.0} ms over {2} ticks; kills {3}, deaths {4}" -f `
        ($t.p99Us / 1000), ($summary.tickPeriodUs / 1000), $t.ticks, $summary.totals.kills, $summary.totals.deaths)
}

# The M3 counters of the economy report (job XP, items by M3 reason, harvests) from this run's telemetry.
Write-Host '== m3-exit: economy report, M3 section'
$report = & dotnet (Join-Path $repo 'artifacts/bin/BotClient/debug/BotClient.dll') report --telemetry (Join-Path $runDir 'telemetry') `
    --kind economy --repo $repo --json (Join-Path $runDir 'economy.json')
$report | Where-Object { $_ -match '\] (M3 |items (created|destroyed) by reason)' } | ForEach-Object { Write-Host $_ }
Write-Host "economy report: $(Join-Path $runDir 'economy.json')"
exit $code
