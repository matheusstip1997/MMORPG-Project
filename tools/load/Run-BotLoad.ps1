<#
.SYNOPSIS
    Local bot load test (backlog J, spec 11.5; M1 exit criterion "tick time p99 within the budget with 50 bots", 12.4).

.DESCRIPTION
    Starts docker compose (PostgreSQL, Redis), builds the GameServer, the Auth API, DbMigrate and the BotClient in Debug
    (the dev login is Debug only), migrates the dev databases, then runs `BotClient loadtest`: it starts both servers
    on free ports with secrets generated for the run, plays the scenario's bots, and reads the tick p50/p99 from the
    server's per-tick telemetry. Everything lands in artifacts/load/<run-id>/ (summary.json, server logs, telemetry).
    Exit code 0 only when every pass criterion of the scenario holds. See src/Tools/BotClient/README.md.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/load/Run-BotLoad.ps1
    powershell -ExecutionPolicy Bypass -File tools/load/Run-BotLoad.ps1 -Link rtt200 -DbSuffix wp15
#>
param(
    # A BotScenario row: m1-exit (50 bots, 5 min), ci-smoke, ci-merge, m2-zone, m2-loop, m2-exit, m3-loop, m3-exit.
    [string]$Scenario = 'm1-exit',
    # A BotLinkProfile row instead of the scenario's: lan, rtt100, rtt200, rtt200-loss2.
    [string]$Link = '',
    # Own dev databases mmorpg_global_<suffix> and mmorpg_realm_1_<suffix> (DbMigrate --db-suffix).
    [ValidatePattern('^[a-z0-9_]{0,32}$')]
    [string]$DbSuffix = '',
    [string]$RunId = '',
    [int]$Bots = 0,
    [int]$DurationMs = 0,
    # A BotBehaviour row instead of the scenario's (for example m2-loop for the m2-exit before baseline).
    [string]$Behaviour = '',
    # The level seed instead of the scenario's seedLevel (WP-M3-12: m3-exit seeds 12; 0 keeps the scenario's).
    [int]$SeedLevel = 0,
    # Skip docker compose (the services are already up).
    [switch]$NoDocker
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ($RunId.Length -eq 0) {
    $RunId = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd-HHmmss') + '-' + $Scenario
    if ($Link.Length -gt 0) { $RunId += '-' + $Link }
}
$runDir = Join-Path $repo (Join-Path 'artifacts\load' $RunId)
New-Item -ItemType Directory -Force $runDir | Out-Null

function Invoke-Step([string]$Name, [scriptblock]$Body) {
    Write-Host "== $Name"
    & $Body
    if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit $LASTEXITCODE)" }
}

Push-Location $repo
try {
    if (-not $NoDocker) {
        # The databases only: the load test starts its own servers (the composed ones would only add load).
        Invoke-Step 'docker compose up' { docker compose up -d --wait postgres redis }
    }
    foreach ($project in 'src/GameServer', 'src/AuthApi', 'src/Tools/DbMigrate', 'src/Tools/BotClient') {
        Invoke-Step "build $project" { dotnet build $project --configuration Debug --nologo -v q }
    }
    $migrate = @('run', '--project', 'src/Tools/DbMigrate', '--no-build', '--configuration', 'Debug', '--', '--dev')
    if ($DbSuffix.Length -gt 0) { $migrate += @('--db-suffix', $DbSuffix) }
    Invoke-Step 'migrate' { dotnet @migrate }

    $arguments = @('artifacts/bin/BotClient/debug/BotClient.dll', 'loadtest', '--scenario', $Scenario, '--run-dir', $runDir)
    if ($Link.Length -gt 0) { $arguments += @('--link', $Link) }
    if ($DbSuffix.Length -gt 0) { $arguments += @('--db-suffix', $DbSuffix) }
    if ($Bots -gt 0) { $arguments += @('--bots', "$Bots") }
    if ($DurationMs -gt 0) { $arguments += @('--duration-ms', "$DurationMs") }
    if ($Behaviour.Length -gt 0) { $arguments += @('--behaviour', $Behaviour) }
    if ($SeedLevel -gt 0) { $arguments += @('--seed-level', "$SeedLevel") }
    Write-Host "== loadtest $Scenario (results in $runDir)"
    & dotnet @arguments
    $code = $LASTEXITCODE

    $summary = Get-Content (Join-Path $runDir 'summary.json') -Raw | ConvertFrom-Json
    if ($null -ne $summary.ticks) {
        $t = $summary.ticks
        Write-Host ("tick p50 {0:0.00} ms, p99 {1:0.00} ms, max {2:0.00} ms over {3} ticks (budget {4:0.0} ms per tick)" -f `
            ($t.p50Us / 1000), ($t.p99Us / 1000), ($t.maxUs / 1000), $t.ticks, ($summary.tickPeriodUs / 1000))
    }
    exit $code
}
finally {
    Pop-Location
}
