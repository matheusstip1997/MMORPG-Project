<#
.SYNOPSIS
    The M2 exit criterion as one command (spec 12.2: "a new character can play from level 1 to about 30 through the loop";
    WP-M2-12, spec 11.5).

.DESCRIPTION
    Runs tools/load/Run-BotLoad.ps1 with the m2-exit scenario: docker compose (PostgreSQL, Redis), Debug builds, migration,
    then `BotClient loadtest --scenario m2-exit`: 5 new characters (level 1) play the M2 loop (fight, pick up, sell, buy,
    run, learn, equip; steps not in the game yet log "not available") until every one reaches level 30 or the time limit
    (BotScenario.durationMs, 4 h). Prints the level of each bot over time, who reached level 30 and when, and the tick p99
    from the TickTiming telemetry like m1-exit. Results in artifacts/load/<run-id>/summary.json.
    Until the protocol carries the own level (WP-M2-3) the report says "level not in protocol yet" and the level criterion
    fails; the run then ends on the time limit (-DurationMs for a short check). Exit code 0 only when every criterion holds.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/load/Run-M2Exit.ps1 -DbSuffix wpm212
    powershell -ExecutionPolicy Bypass -File tools/load/Run-M2Exit.ps1 -DbSuffix wpm212 -DurationMs 180000 -Link rtt100
#>
param(
    # A BotLinkProfile row instead of the scenario's (lan): rtt100, rtt200, rtt200-loss2.
    [string]$Link = '',
    # Own dev databases mmorpg_global_<suffix> and mmorpg_realm_1_<suffix> (DbMigrate --db-suffix).
    [ValidatePattern('^[a-z0-9_]{0,32}$')]
    [string]$DbSuffix = '',
    [string]$RunId = '',
    [int]$Bots = 0,
    # The time limit instead of the scenario's 4 hours.
    [int]$DurationMs = 0,
    # Skip docker compose (the services are already up).
    [switch]$NoDocker
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ($RunId.Length -eq 0) {
    $RunId = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd-HHmmss') + '-m2-exit'
    if ($Link.Length -gt 0) { $RunId += '-' + $Link }
}
$runDir = Join-Path $repo (Join-Path 'artifacts\load' $RunId)

# Run-BotLoad.ps1 ends with `exit`, so it runs in a child PowerShell; its output streams through.
$arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'Run-BotLoad.ps1'),
    '-Scenario', 'm2-exit', '-RunId', $RunId)
if ($Link.Length -gt 0) { $arguments += @('-Link', $Link) }
if ($DbSuffix.Length -gt 0) { $arguments += @('-DbSuffix', $DbSuffix) }
if ($Bots -gt 0) { $arguments += @('-Bots', "$Bots") }
if ($DurationMs -gt 0) { $arguments += @('-DurationMs', "$DurationMs") }
if ($NoDocker) { $arguments += '-NoDocker' }
& powershell @arguments
$code = $LASTEXITCODE

$summaryPath = Join-Path $runDir 'summary.json'
if (-not (Test-Path $summaryPath)) {
    Write-Host "m2-exit: no summary.json in $runDir (the run did not get to the bots)"
    exit $(if ($code -ne 0) { $code } else { 1 })
}
$summary = Get-Content $summaryPath -Raw | ConvertFrom-Json
$levels = $summary.levels
Write-Host '== m2-exit'
if (-not $levels.anyLevel) {
    if ($levels.levelInProtocol) { Write-Host 'level per bot: no bot received a level' }
    else { Write-Host 'level per bot: level not in protocol yet (no own-entity level message; WP-M2-3)' }
}
else {
    foreach ($sample in $levels.samples) {
        $row = ($sample.levels | ForEach-Object { if ($null -eq $_) { '?' } else { "$_" } }) -join ' '
        Write-Host ("  {0,6} s: {1}" -f $sample.atSeconds, $row)
    }
}
$criterion = $summary.criteria | Where-Object { $_.name -like 'bots reached level*' } | Select-Object -First 1
if ($null -ne $criterion) {
    $verdict = if ($criterion.pass) { 'PASS' } else { 'FAIL' }
    Write-Host ("[{0}] {1}: expected {2}, got {3}" -f $verdict, $criterion.name, $criterion.expected, $criterion.actual)
}
if ($null -ne $summary.ticks) {
    $t = $summary.ticks
    Write-Host ("tick p99 {0:0.00} ms of {1:0.0} ms over {2} ticks; kills per bot-minute {3}; loop rounds {4}" -f `
        ($t.p99Us / 1000), ($summary.tickPeriodUs / 1000), $t.ticks, $summary.totals.killsPerBotMinute, $summary.loop.rounds)
}
exit $code
