<#
.SYNOPSIS
    The M4 exit run as one command (spec 12.2 "no duplication in concurrency tests; the economy dashboard shows faucet and
    sink totals"; docs/plans/m4-player-economy-plan.md section 1, the seven legs; WP-M4-12, spec 11.5).

.DESCRIPTION
    Runs tools/load/Run-BotLoad.ps1 with the m4-exit scenario: docker compose (PostgreSQL, Redis), Debug builds, migration,
    then `BotClient loadtest --scenario m4-exit`: two fresh characters as a pair (BotScenario.pairs), seeded to level 25
    (DbMigrate --set-level, a test seed; TODO(open) decision 14), play one script each (BotBehaviour loopOnce): A (warrior)
    invites B to a party, hunts with it and the pickup rolls, trades the item to B for gold, buys B's listing, opens a stall
    that B buys from, dies twice (recovers one bag, lets one expire), forges B's sword contract and claims a returned listing
    by mail; B accepts, pays the trade, lists, claims the proceeds, buys from the stall, mines ore and proposes the contract.
    Prints every script entry as done, not yet (with the reason: the WP that lands it) or failed, the pair's meetings, then
    the M4 section of the economy report (fees, trades, listings, stalls, loot bags, contracts, party rolls, mail, the M4
    reasons) from the run's telemetry. Results in artifacts/load/<run-id>/summary.json and economy.json.
    Exit code 0 only when every entry is done and every other criterion holds.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/load/Run-M4Exit.ps1 -DbSuffix wpm412
    powershell -ExecutionPolicy Bypass -File tools/load/Run-M4Exit.ps1 -DbSuffix wpm412 -NoDocker -Link rtt100
#>
param(
    # A BotLinkProfile row instead of the scenario's (lan): rtt100, rtt200, rtt200-loss2.
    [string]$Link = '',
    # Own dev databases mmorpg_global_<suffix> and mmorpg_realm_1_<suffix> (DbMigrate --db-suffix).
    [ValidatePattern('^[a-z0-9_]{0,32}$')]
    [string]$DbSuffix = '',
    [string]$RunId = '',
    # The time limit instead of the scenario's 60 minutes (the run ends earlier once both scripts finished).
    [int]$DurationMs = 0,
    # Skip docker compose (the services are already up).
    [switch]$NoDocker
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ($RunId.Length -eq 0) {
    $RunId = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd-HHmmss') + '-m4-exit'
    if ($Link.Length -gt 0) { $RunId += '-' + $Link }
}
$runDir = Join-Path $repo (Join-Path 'artifacts\load' $RunId)

# Run-BotLoad.ps1 ends with `exit`, so it runs in a child PowerShell; its output streams through.
$arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'Run-BotLoad.ps1'),
    '-Scenario', 'm4-exit', '-RunId', $RunId)
if ($Link.Length -gt 0) { $arguments += @('-Link', $Link) }
if ($DbSuffix.Length -gt 0) { $arguments += @('-DbSuffix', $DbSuffix) }
if ($DurationMs -gt 0) { $arguments += @('-DurationMs', "$DurationMs") }
if ($NoDocker) { $arguments += '-NoDocker' }
& powershell @arguments
$code = $LASTEXITCODE

$summaryPath = Join-Path $runDir 'summary.json'
if (-not (Test-Path $summaryPath)) {
    Write-Host "m4-exit: no summary.json in $runDir (the run did not get to the bots)"
    exit $(if ($code -ne 0) { $code } else { 1 })
}
$summary = Get-Content $summaryPath -Raw | ConvertFrom-Json
Write-Host '== m4-exit: the exit run per script entry (the reason of a "not yet" names the WP that lands it)'
foreach ($bot in $summary.botsDetail) {
    $pair = if ($bot.partner -ge 0) { ", pair with bot$($bot.partner)" } else { '' }
    Write-Host ("bot{0} {1} ({2} {3}, level {4}{5}):" -f $bot.index, $bot.behaviour, $bot.classId, $bot.character, $bot.level, $pair)
    if ($null -eq $bot.script) { Write-Host '  no script (not a loopOnce behaviour)'; continue }
    foreach ($step in $bot.script) {
        $line = "  [{0}] {1}" -f $step.outcome, $step.entry
        if ($step.reason.Length -gt 0) { $line += ': ' + $step.reason }
        Write-Host $line
    }
}
# The pair's meetings (the meet step's counters: met, gave up, gone, late).
foreach ($bot in $summary.botsDetail) {
    $met = $bot.loop.'meet.met'
    if ($null -ne $met -or $null -ne $bot.loop.'meet.late') {
        Write-Host ("bot{0} meetings: met {1}, partner gave up {2}, partner gone {3}, late {4}" -f $bot.index, [int]$met,
            [int]$bot.loop.'meet.gaveUp', [int]$bot.loop.'meet.gone', [int]$bot.loop.'meet.late')
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

# The M4 counters of the economy report from this run's telemetry (zeros until the WPs write the records).
Write-Host '== m4-exit: economy report, M4 section'
$report = & dotnet (Join-Path $repo 'artifacts/bin/BotClient/debug/BotClient.dll') report --telemetry (Join-Path $runDir 'telemetry') `
    --kind economy --repo $repo --json (Join-Path $runDir 'economy.json')
$report | Where-Object { $_ -match '\] (M4 |total: )' } | ForEach-Object { Write-Host $_ }
Write-Host "summary: $summaryPath"
Write-Host "economy report: $(Join-Path $runDir 'economy.json')"
exit $code
