<#
.SYNOPSIS
    QA smoke run (backlog J): starts the local stack, runs the live end-to-end test and the scripted smoke scenario in
    Unity, stops everything it started, and prints a summary. See tools/qa/README.md.

.DESCRIPTION
    1. Checks that no Unity editor has the project open and that ports 5080 (Auth API) and 7777 (GameServer) are free.
    2. docker compose up -d --wait postgres redis: the databases only, never the composed servers (stopped again at the
       end only if this script started them).
    3. Builds GameServer, AuthApi and DbMigrate once, then DbMigrate --dev (with -DbSuffix: --db-suffix, own databases).
    4. Starts the GameServer and the Auth API (dev login) from the build output with secrets generated for this run only.
    5. Unity EditMode LoginEndToEndTests, then Unity PlayMode QaSmokeScenarioTests (batch mode, with graphics).
    6. The saved bag: item_stacks in the realm database holds the stack the move-stack step moved (WP16).
    7. Stops both servers (always), writes summary.json and prints the step table.
    Outputs go to artifacts/qa/<RunId>/ (git-ignored). Exit code 0 only when everything passed.
    Windows PowerShell 5.1 or PowerShell 7.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/qa/Run-QaSmoke.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/qa/Run-QaSmoke.ps1 -Class archer
#>
[CmdletBinding()]
param(
    # Folder name under artifacts/qa; default <yyyy-MM-dd-HHmmss>-smoke.
    [string]$RunId = ((Get-Date -Format 'yyyy-MM-dd-HHmmss') + '-smoke'),
    # Skip the live end-to-end EditMode test (LoginEndToEndTests).
    [switch]$SkipEndToEnd,
    # Dev login id (dev:<n>) for the scenario; 0 picks a new one, so a new character is created at the city spawn.
    [long]$DevLogin = 0,
    # Class of the character the scenario creates (a ClassDef id, for example archer); empty: the first allowed class
    # (warrior). The arrow-wall step is n/a for a kit that shoots nothing (the warrior's).
    [string]$Class = '',
    [string]$UnityPath = 'C:\Program Files\Unity\Hub\Editor\6000.3.26f1\Editor\Unity.exe',
    # Minutes before a Unity run is killed.
    [int]$UnityTimeoutMinutes = 20,
    # Stop servers and Unity batch runs that an interrupted earlier run of this checkout left behind (else: fail).
    [switch]$StopLeftovers,
    # Own dev databases for this checkout: mmorpg_global_<suffix> and mmorpg_realm_1_<suffix> (DbMigrate --db-suffix) and
    # Redis keys under mmorpg:<suffix>:, so a branch with new migrations never touches the shared dev databases.
    # Empty: the shared mmorpg_global and mmorpg_realm_1.
    [ValidatePattern('^[a-z0-9_]{0,32}$')]
    [string]$DbSuffix = '',
    # Minutes to wait for another checkout's smoke run on this machine to finish (they share ports 7777 and 5080).
    [int]$LockWaitMinutes = 30,
    # WP-M2-11: after the migration, put a stack of 5 demon ash into the Normal bag of the -DevLogin account's characters
    # (tools/qa/Set-BagItem.ps1, test data), so the split-stack step has a stack to split, and (WP-M3-3) three small HP
    # potions for the drink-potion step. WP-M3-6: four ore and two cyclops hides for the forge-sword step. Needs a -DevLogin that already has a character (a second run with the same
    # -DevLogin).
    [switch]$SeedStack
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$runDir = Join-Path $repo ("artifacts\qa\" + $RunId)
New-Item -ItemType Directory -Force $runDir | Out-Null
$clock = [Diagnostics.Stopwatch]::StartNew()
$phases = New-Object System.Collections.Generic.List[object]
$started = @{}
$runLock = $null
$composeWasRunning = $false
$composeStarted = $false
$savedEnv = @{}
$dbTail = ''
if ($DbSuffix.Length -gt 0) { $dbTail = '_' + $DbSuffix }
$globalDb = 'mmorpg_global' + $dbTail
$realmDb = 'mmorpg_realm_1' + $dbTail
$migrateArgs = @('run', '--no-build', '--project', 'src/Tools/DbMigrate', '--', '--dev')
if ($DbSuffix.Length -gt 0) { $migrateArgs += @('--db-suffix', $DbSuffix) }

function Write-Phase([string]$Name, [string]$Status, [double]$Seconds, [string]$Detail) {
    $phases.Add([ordered]@{ name = $Name; status = $Status; seconds = [math]::Round($Seconds, 1); detail = $Detail })
    $color = 'Green'
    if ($Status -eq 'fail') { $color = 'Red' } elseif ($Status -ne 'pass') { $color = 'Yellow' }
    Write-Host ("[qa] {0,-14} {1,-7} {2,6:0.0} s  {3}" -f $Name, $Status, $Seconds, $Detail) -ForegroundColor $color
}

function Invoke-Phase([string]$Name, [scriptblock]$Body) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $detail = & $Body
        Write-Phase $Name 'pass' $watch.Elapsed.TotalSeconds ([string]$detail)
    } catch {
        Write-Phase $Name 'fail' $watch.Elapsed.TotalSeconds $_.Exception.Message
        throw
    }
}

function Set-RunEnv([string]$Name, [string]$Value) {
    if (-not $savedEnv.ContainsKey($Name)) { $savedEnv[$Name] = [Environment]::GetEnvironmentVariable($Name, 'Process') }
    [Environment]::SetEnvironmentVariable($Name, $Value, 'Process')
}

function New-Secret {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($bytes)
}

# Native tools run through Start-Process with their output in the run folder: Windows PowerShell 5.1 turns native stderr
# into errors, which would stop the script under ErrorActionPreference Stop.
function Invoke-Logged([string]$File, [string[]]$Arguments, [string]$Log) {
    $out = Join-Path $runDir $Log
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -WorkingDirectory $repo -NoNewWindow -PassThru `
        -RedirectStandardOutput $out -RedirectStandardError ($out + '.err')
    $null = $process.Handle # keeps the exit code readable after exit (Windows PowerShell 5.1)
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "$File $($Arguments -join ' ') exited with $($process.ExitCode); see $Log" }
    return Get-Content -Raw $out -ErrorAction SilentlyContinue
}

function Test-ProjectLocked {
    $lock = Join-Path $repo 'unity\Temp\UnityLockfile'
    if (-not (Test-Path $lock)) { return $false }
    try {
        $stream = [IO.File]::Open($lock, 'Open', 'ReadWrite', 'None')
        $stream.Close()
        return $false
    } catch {
        return $true
    }
}

# Processes an interrupted run of this checkout can leave (a killed script never reaches its cleanup): the servers
# started from artifacts/bin and Unity batch test runs on unity/.
function Get-Leftovers {
    $bin = (Join-Path $repo 'artifacts\bin').ToLowerInvariant()
    $project = (Join-Path $repo 'unity').ToLowerInvariant()
    @(Get-CimInstance Win32_Process | Where-Object {
        $line = [string]$_.CommandLine
        $lower = $line.ToLowerInvariant()
        ($_.Name -eq 'dotnet.exe' -and $lower.Contains($bin) -and ($lower.Contains('gameserver.dll') -or $lower.Contains('authapi.dll'))) -or
        ($_.Name -eq 'Unity.exe' -and $lower.Contains('-runtests') -and $lower.Contains('"' + $project + '"'))
    })
}

function Wait-Until([string]$What, [int]$Seconds, [scriptblock]$Condition, $Process) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return }
        if ($null -ne $Process -and $Process.HasExited) { throw "$What exited early with code $($Process.ExitCode)" }
        Start-Sleep -Milliseconds 500
    }
    throw "timed out after $Seconds s waiting for $What"
}

function Start-Server([string]$Name, [string]$Dll, [string]$Log) {
    $out = Join-Path $runDir $Log
    $process = Start-Process -FilePath 'dotnet' -ArgumentList ('"' + $Dll + '"') -WorkingDirectory $repo -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $out -RedirectStandardError ($out + '.err')
    $null = $process.Handle # keeps the exit code readable after exit (Windows PowerShell 5.1)
    $started[$Name] = $process
    return $process
}

# QA wave 5 H4: the port must be bound by this run's own server (another checkout's may hold it). The dotnet host runs
# the dll in its own process, so the owning process id is the one Start-Process returned.
function Test-OwnUdp([int]$Port, $Process) {
    [bool](@(Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue) | Where-Object { $_.OwningProcess -eq $Process.Id })
}

function Test-OwnTcpListen([int]$Port, $Process) {
    [bool](@(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) | Where-Object { $_.OwningProcess -eq $Process.Id })
}

function Invoke-Unity([string]$Name, [string[]]$Arguments, [string]$Results, [string]$Log) {
    # Parentheses: the comma binds tighter than +, and Start-Process needs the quotes around paths.
    $all = @('-batchmode', '-projectPath', ('"' + (Join-Path $repo 'unity') + '"'), '-runTests') + $Arguments +
        @('-testResults', ('"' + (Join-Path $runDir $Results) + '"'), '-logFile', ('"' + (Join-Path $runDir $Log) + '"'))
    $process = Start-Process -FilePath $UnityPath -ArgumentList $all -PassThru -WindowStyle Hidden
    $null = $process.Handle
    $started[$Name] = $process
    if (-not $process.WaitForExit($UnityTimeoutMinutes * 60 * 1000)) {
        Stop-Process -Id $process.Id -Force
        throw "Unity did not finish in $UnityTimeoutMinutes min; see $Log"
    }
    $process.WaitForExit()
    $started.Remove($Name)
    $summary = Read-TestResults (Join-Path $runDir $Results)
    # Unity: 0 all passed, 2 some tests failed, anything else is a run error (compile errors, crash).
    if ($process.ExitCode -ne 0) { throw "exit code $($process.ExitCode): $summary; see $Log" }
    return $summary
}

function Read-TestResults([string]$Path) {
    if (-not (Test-Path $Path)) { return 'no test results' }
    [xml]$xml = Get-Content -Raw $Path
    $run = $xml.'test-run'
    return "$($run.passed) passed, $($run.failed) failed, $($run.skipped) skipped"
}

$exitCode = 1
try {
    Write-Host "[qa] run folder $runDir"
    Invoke-Phase 'preflight' {
        # Machine-wide: smoke runs of other checkouts use the same fixed ports, so they take turns (QA wave 5 H4).
        $script:runLock = New-Object System.Threading.Mutex($false, 'Global\MMORPG-QaSmoke')
        try {
            if (-not $script:runLock.WaitOne(0)) {
                Write-Host "[qa] another smoke run holds the machine-wide lock; waiting up to $LockWaitMinutes min" -ForegroundColor Yellow
                if (-not $script:runLock.WaitOne([TimeSpan]::FromMinutes($LockWaitMinutes))) {
                    $script:runLock.Dispose()
                    $script:runLock = $null
                    throw "another smoke run kept the machine-wide lock for $LockWaitMinutes min"
                }
            }
        } catch [System.Threading.AbandonedMutexException] {
            Write-Host '[qa] took over the lock of a smoke run that ended without releasing it' -ForegroundColor Yellow
        }
        if (-not (Test-Path $UnityPath)) { throw "Unity not found at $UnityPath (pass -UnityPath)" }
        $leftovers = @(Get-Leftovers) # @(): an empty result comes back as $null
        if ($leftovers.Count -gt 0) {
            $list = ($leftovers | ForEach-Object { "$($_.Name) pid $($_.ProcessId)" }) -join ', '
            if (-not $StopLeftovers) { throw "an earlier run left $list; stop them or pass -StopLeftovers" }
            foreach ($process in $leftovers) { Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue }
            Start-Sleep -Seconds 2
            Write-Host "[qa] stopped leftovers: $list" -ForegroundColor Yellow
        }
        if (Test-ProjectLocked) { throw 'a Unity editor has unity/ open; close it (batch runs need the project to themselves)' }
        $composed = 'if it is the composed stack: docker compose stop authapi gameserver adminpanel'
        if (Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction SilentlyContinue) { throw "port 5080 is in use (an Auth API already running? $composed)" }
        if (Get-NetUDPEndpoint -LocalPort 7777 -ErrorAction SilentlyContinue) { throw "UDP port 7777 is in use (a GameServer already running? $composed)" }
        if (-not (Test-Path (Join-Path $repo 'unity\Library'))) { Write-Host '[qa] unity/Library is missing: the first Unity run imports the whole project (several minutes)' -ForegroundColor Yellow }
        'editor closed, ports free'
    }

    Invoke-Phase 'docker' {
        # Only the databases: this run starts its own servers on 5080 and 7777 (the composed ones would hold them).
        $running = [string](Invoke-Logged 'docker' @('compose', 'ps', '--status', 'running', '-q', 'postgres', 'redis') 'docker-ps.log')
        $script:composeWasRunning = $running.Trim().Length -gt 0
        $null = Invoke-Logged 'docker' @('compose', 'up', '-d', '--wait', 'postgres', 'redis') 'docker.log'
        $script:composeStarted = -not $script:composeWasRunning
        if ($script:composeWasRunning) { 'already running (left running)' } else { 'started (stopped at the end)' }
    }

    Invoke-Phase 'build' {
        foreach ($project in @('src/GameServer', 'src/AuthApi', 'src/Tools/DbMigrate')) {
            $null = Invoke-Logged 'dotnet' @('build', $project, '-nologo', '-v', 'q') ('build-' + (Split-Path $project -Leaf) + '.log')
        }
        'GameServer, AuthApi, DbMigrate'
    }

    Invoke-Phase 'migrate' {
        $null = Invoke-Logged 'dotnet' $migrateArgs 'dbmigrate.log'
        # wait-respawn (QA wave 1): the demand loop's spawn percents (R-4.2-03) as the GameServer loads them at start, for
        # world 1 (Bootstrap.MainWorldId). An earlier run on these databases may have moved them; the scenario follows this
        # run's changes in the telemetry (DemandUpdated). Read only.
        $sql = "SELECT def_id, region_id, spawn_percent FROM demand_state WHERE world_id = 1 AND key_kind = 0 ORDER BY def_id, region_id"
        $rows = @(& docker compose exec -T postgres psql -U mmorpg -d $realmDb -At -F ',' -v ON_ERROR_STOP=1 -c $sql)
        if ($LASTEXITCODE -ne 0) { throw "psql exited with $LASTEXITCODE reading demand_state" }
        $demand = @($rows | Where-Object { $_ } | ForEach-Object {
            $field = $_.Split(',')
            [ordered]@{ defId = $field[0]; regionId = $field[1]; spawnPercent = [int]$field[2] }
        })
        [ordered]@{ rows = $demand } | ConvertTo-Json -Depth 3 | Set-Content -Encoding UTF8 (Join-Path $runDir 'demand-start.json')
        "$globalDb, $realmDb; $($demand.Count) demand counters"
    }

    # WP-M2-11: a stack for the split-stack step (test data, before the servers load the character).
    if ($SeedStack -and $DevLogin -gt 0) {
        Invoke-Phase 'seed-stack' {
            $seedArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'Set-BagItem.ps1'),
                '-DevLogin', "$DevLogin")
            if ($DbSuffix.Length -gt 0) { $seedArgs += @('-DbSuffix', $DbSuffix) }
            $null = Invoke-Logged 'powershell' $seedArgs 'seed-stack.log'
            # WP-M3-3: three small HP potions for the drink-potion step.
            $null = Invoke-Logged 'powershell' ($seedArgs + @('-Item', 'm2.potionHpSmall', '-Count', '3')) 'seed-potions.log'
            # WP-M3-5: a pickaxe in the Safe bag's equipment area for the gather-ore step (once per character).
            $toolArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'Set-BagInstance.ps1'),
                '-DevLogin', "$DevLogin", '-Item', 'm3.pickaxeT1')
            if ($DbSuffix.Length -gt 0) { $toolArgs += @('-DbSuffix', $DbSuffix) }
            $null = Invoke-Logged 'powershell' $toolArgs 'seed-pickaxe.log'
            # WP-M3-6: ore and a hide grip for the forge-sword step (two refines and the sword).
            $null = Invoke-Logged 'powershell' ($seedArgs + @('-Item', 'm3.oreT1', '-Count', '4')) 'seed-ore.log'
            $null = Invoke-Logged 'powershell' ($seedArgs + @('-Item', 'm1.cyclopsHide', '-Count', '2')) 'seed-hide.log'
            # WP-M3-8: two enchant stones for the enchant-sword step (one per try at tier 1; a rerun gets another try).
            $null = Invoke-Logged 'powershell' ($seedArgs + @('-Item', 'm3.enchantStone', '-Count', '2')) 'seed-stone.log'
            "dev:$DevLogin (seed-stack.log, seed-potions.log, seed-pickaxe.log, seed-ore.log, seed-hide.log, seed-stone.log)"
        }
    } elseif ($SeedStack) {
        Write-Phase 'seed-stack' 'skipped' 0 '-SeedStack needs a -DevLogin with a character'
    }

    Invoke-Phase 'servers' {
        # Same ticket secret for the Auth API (issues tickets) and the GameServer (checks them); never written to disk.
        Set-RunEnv 'MMORPG_TICKET_SECRET' (New-Secret)
        Set-RunEnv 'MMORPG_TICKET_KEY_ID' '1'
        Set-RunEnv 'MMORPG_REALM_ID' '1'
        Set-RunEnv 'MMORPG_GAME_PORT' '7777'
        Set-RunEnv 'MMORPG_GLOBAL_DB' "Host=127.0.0.1;Port=5432;Username=mmorpg;Password=mmorpg_dev;Database=$globalDb"
        Set-RunEnv 'MMORPG_REALM_DB' "Host=127.0.0.1;Port=5432;Username=mmorpg;Password=mmorpg_dev;Database=$realmDb"
        Set-RunEnv 'MMORPG_REDIS' '127.0.0.1:6379'
        if ($DbSuffix.Length -gt 0) { Set-RunEnv 'MMORPG_REDIS_PREFIX' "mmorpg:${DbSuffix}:" }
        Set-RunEnv 'MMORPG_TELEMETRY_DIR' (Join-Path $runDir 'telemetry')
        # Spec 11.7: the GameServer's dev tools (table hot reload, strike trace) on, and the servers and Unity on one copy of
        # the tables that the dev-tools step may edit, so the run never touches data/tables.
        $tablesCopy = Join-Path $runDir 'tables'
        Copy-Item -Recurse -Path (Join-Path $repo 'data\tables') -Destination $tablesCopy
        Set-RunEnv 'MMORPG_DATA_DIR' $tablesCopy
        Set-RunEnv 'MMORPG_DEV_TOOLS' '1'
        Set-RunEnv 'MMORPG_AUTH_TOKEN_SECRET' (New-Secret)
        Set-RunEnv 'MMORPG_AUTH_IDENTITY' 'dev'
        $bin = Join-Path $repo 'artifacts\bin'
        $server = Start-Server 'GameServer' (Join-Path $bin 'GameServer\debug\GameServer.dll') 'gameserver.log'
        $auth = Start-Server 'AuthApi' (Join-Path $bin 'AuthApi\debug\AuthApi.dll') 'authapi.log'
        Wait-Until "the GameServer (pid $($server.Id)) on UDP 7777" 60 { Test-OwnUdp 7777 $server } $server
        Wait-Until "the Auth API (pid $($auth.Id)) on TCP 5080" 60 { Test-OwnTcpListen 5080 $auth } $auth
        # Both still alive once both listen (one that failed to bind may exit just after the other started).
        foreach ($process in @($server, $auth)) {
            if ($process.HasExited) { throw "pid $($process.Id) exited with code $($process.ExitCode); see gameserver.log and authapi.log" }
        }
        "GameServer pid $($server.Id), Auth API pid $($auth.Id)"
    }

    $failures = 0
    if ($SkipEndToEnd) {
        Write-Phase 'end-to-end' 'skipped' 0 '-SkipEndToEnd'
    } else {
        try {
            Invoke-Phase 'end-to-end' {
                Set-RunEnv 'MMORPG_E2E_AUTH_URL' 'http://127.0.0.1:5080'
                Invoke-Unity 'UnityEndToEnd' @('-testPlatform', 'EditMode', '-testFilter', 'LoginEndToEndTests') 'e2e-results.xml' 'unity-e2e.log'
            }
        } catch { $failures++ }
    }

    if ($DevLogin -le 0) { $DevLogin = Get-Random -Minimum 2000000 -Maximum 2999999 }
    try {
        Invoke-Phase 'smoke' {
            Set-RunEnv 'MMORPG_QA_RUN_DIR' $runDir
            $smokeArgs = @('-testPlatform', 'PlayMode', '-testFilter', 'QaSmokeScenarioTests', '-devLogin', "$DevLogin")
            if ($Class.Length -gt 0) { $smokeArgs += @('-qaClass', $Class) }
            Invoke-Unity 'UnitySmoke' $smokeArgs 'smoke-results.xml' 'unity-smoke.log'
        }
    } catch { $failures++ }

    # WP16: the bag as the database holds it, not as the client was told: the stack the move-stack step moved.
    $bagResult = $null
    $resultPath = Join-Path $runDir 'result.json'
    if (Test-Path $resultPath) { $bagResult = Get-Content -Raw $resultPath | ConvertFrom-Json }
    if ($null -eq $bagResult -or -not ($bagResult.PSObject.Properties.Name -contains 'bagItemId') -or $bagResult.bagItemId.Length -eq 0) {
        Write-Phase 'saved-bag' 'skipped' 0 'the move-stack step did not move a stack'
    } else {
        try {
            Invoke-Phase 'saved-bag' {
                # The container the step moved in: stacks, and instances (equipment, count 1) for the Safe bag's equipment area.
                $container = 1
                if ($bagResult.PSObject.Properties.Name -contains 'bagContainer') { $container = [int]$bagResult.bagContainer }
                $owner = "(SELECT character_id FROM inventory_ops WHERE op_key LIKE 'move:%' ORDER BY created_at DESC LIMIT 1)"
                $sql = "SELECT slot || ':' || def_id || ':' || count FROM item_stacks WHERE container = $container AND character_id = $owner " +
                    "UNION ALL SELECT slot || ':' || def_id || ':1' FROM item_instances WHERE container = $container AND character_id = $owner ORDER BY 1"
                $rows = & docker compose exec -T postgres psql -U mmorpg -d $realmDb -At -v ON_ERROR_STOP=1 -c $sql
                if ($LASTEXITCODE -ne 0) { throw "psql exited with $LASTEXITCODE" }
                $rows = @($rows | Where-Object { $_ -and $_.Trim().Length -gt 0 } | ForEach-Object { $_.Trim() })
                $rows -join "`n" | Out-File -Encoding utf8 (Join-Path $runDir 'saved-bag.txt')
                $expected = "$($bagResult.bagSlot):$($bagResult.bagItemId):$($bagResult.bagCount)"
                $left = @($rows | Where-Object { $_ -like "$($bagResult.bagFromSlot):$($bagResult.bagItemId):*" })
                if (-not ($rows -contains $expected)) { throw "container $container has no $expected (slot:item:count); rows: $($rows -join ', ')" }
                if ($left.Count -gt 0) { throw "the stack is still in slot $($bagResult.bagFromSlot): $($left -join ', ')" }
                "$expected in container $container of $realmDb"
            }
        } catch { $failures++ }
    }

    if ($failures -eq 0) { $exitCode = 0 }
} catch {
    Write-Host "[qa] stopped: $($_.Exception.Message)" -ForegroundColor Red
} finally {
    foreach ($name in @($started.Keys)) {
        $process = $started[$name]
        if ($null -ne $process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            Write-Host "[qa] stopped $name (pid $($process.Id))"
        }
    }
    if ($composeStarted) {
        try {
            $null = Invoke-Logged 'docker' @('compose', 'stop', 'postgres', 'redis') 'docker-stop.log'
            Write-Host '[qa] stopped docker compose (it was not running before)'
        } catch {
            Write-Host "[qa] docker compose stop failed: $($_.Exception.Message)" -ForegroundColor Red
        }
    }
    foreach ($name in $savedEnv.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnv[$name], 'Process') }
    if ($null -ne $runLock) {
        try { $runLock.ReleaseMutex() } catch { }
        $runLock.Dispose()
    }

    $steps = @()
    $resultFile = Join-Path $runDir 'result.json'
    if (Test-Path $resultFile) {
        $result = Get-Content -Raw $resultFile | ConvertFrom-Json
        Write-Host ''
        Write-Host '[qa] scenario steps'
        foreach ($step in $result.steps) {
            $color = 'Green'
            if ($step.status -eq 'fail') { $color = 'Red' } elseif ($step.status -ne 'pass') { $color = 'Yellow' }
            Write-Host ("  {0:00} {1,-14} {2,-7} {3,7:0.0} s  {4}" -f $step.index, $step.name, $step.status, ($step.durationMs / 1000.0), $step.message) -ForegroundColor $color
            $steps += [ordered]@{ index = $step.index; name = $step.name; status = $step.status; screenshots = @($step.screenshots); message = $step.message }
        }
    }
    $summary = [ordered]@{
        runId = $RunId; runDir = $runDir; passed = ($exitCode -eq 0); seconds = [math]::Round($clock.Elapsed.TotalSeconds, 1)
        devLogin = $DevLogin; class = $Class; phases = $phases; steps = $steps
    }
    $summary | ConvertTo-Json -Depth 6 | Out-File -Encoding utf8 (Join-Path $runDir 'summary.json')
    Write-Host ''
    $verdict = 'FAILED'
    if ($exitCode -eq 0) { $verdict = 'PASSED' }
    Write-Host ("[qa] {0} in {1:0} s; results in {2}" -f $verdict, $clock.Elapsed.TotalSeconds, $runDir)
}
exit $exitCode
