<#
.SYNOPSIS
    QA smoke run (backlog J): starts the local stack, runs the live end-to-end test and the scripted smoke scenario in
    Unity, stops everything it started, and prints a summary. See tools/qa/README.md.

.DESCRIPTION
    1. Checks that no Unity editor has the project open and that ports 5080 (Auth API) and 7777 (GameServer) are free.
    2. docker compose up -d --wait (stopped again at the end only if this script started it).
    3. Builds GameServer, AuthApi and DbMigrate once, then DbMigrate --dev.
    4. Starts the GameServer and the Auth API (dev login) from the build output with secrets generated for this run only.
    5. Unity EditMode LoginEndToEndTests, then Unity PlayMode QaSmokeScenarioTests (batch mode, with graphics).
    6. Stops both servers (always), writes summary.json and prints the step table.
    Outputs go to artifacts/qa/<RunId>/ (git-ignored). Exit code 0 only when everything passed.
    Windows PowerShell 5.1 or PowerShell 7.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/qa/Run-QaSmoke.ps1
#>
[CmdletBinding()]
param(
    # Folder name under artifacts/qa; default <yyyy-MM-dd-HHmmss>-smoke.
    [string]$RunId = ((Get-Date -Format 'yyyy-MM-dd-HHmmss') + '-smoke'),
    # Skip the live end-to-end EditMode test (LoginEndToEndTests).
    [switch]$SkipEndToEnd,
    # Dev login id (dev:<n>) for the scenario; 0 picks a new one, so a new character is created at the city spawn.
    [long]$DevLogin = 0,
    [string]$UnityPath = 'C:\Program Files\Unity\Hub\Editor\6000.3.26f1\Editor\Unity.exe',
    # Minutes before a Unity run is killed.
    [int]$UnityTimeoutMinutes = 20,
    # Stop servers and Unity batch runs that an interrupted earlier run of this checkout left behind (else: fail).
    [switch]$StopLeftovers,
    # Minutes to wait for a run of another checkout (the ports are machine-wide) before giving up.
    [int]$LockTimeoutMinutes = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$runDir = Join-Path $repo ("artifacts\qa\" + $RunId)
New-Item -ItemType Directory -Force $runDir | Out-Null
$clock = [Diagnostics.Stopwatch]::StartNew()
$phases = New-Object System.Collections.Generic.List[object]
$started = @{}
$composeWasRunning = $false
$composeStarted = $false
$savedEnv = @{}

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

# One run per machine: the servers use fixed ports (5080, 7777), so runs from other checkouts queue on a named mutex.
# A run that dies without releasing it leaves it abandoned, and Windows hands it to the next waiter (stale lock). The
# owner file only tells a waiting run whom it waits for.
$lockName = 'Global\MMORPG-QaSmoke'
$lockOwnerFile = Join-Path ([IO.Path]::GetTempPath()) 'mmorpg-qa-smoke.owner'
$lock = $null

function Enter-RunLock {
    $script:lock = New-Object Threading.Mutex($false, $lockName)
    $deadline = (Get-Date).AddMinutes($LockTimeoutMinutes)
    $note = ''
    while ($true) {
        try {
            if ($script:lock.WaitOne([TimeSpan]::FromSeconds(10))) { break }
        } catch [Threading.AbandonedMutexException] {
            $note = ' (the previous run exited without releasing it)'
            break
        }
        $owner = 'another run'
        if (Test-Path $lockOwnerFile) { $owner = (Get-Content -Raw $lockOwnerFile -ErrorAction SilentlyContinue).Trim() }
        if ((Get-Date) -ge $deadline) {
            $script:lock.Dispose()
            $script:lock = $null
            throw "another QA smoke run still holds the machine-wide lock after $LockTimeoutMinutes min: $owner (pass -LockTimeoutMinutes)"
        }
        Write-Host "[qa] waiting for the machine-wide QA lock: $owner" -ForegroundColor Yellow
    }
    Set-Content -Path $lockOwnerFile -Encoding utf8 -Value ("pid $PID, $runDir, since " + (Get-Date -Format 'HH:mm:ss'))
    return "acquired$note"
}

function Exit-RunLock {
    if ($null -eq $script:lock) { return }
    Remove-Item $lockOwnerFile -ErrorAction SilentlyContinue
    $script:lock.ReleaseMutex()
    $script:lock.Dispose()
    $script:lock = $null
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
    $started[$Name] = $process
    return $process
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
    Invoke-Phase 'lock' { Enter-RunLock }
    Invoke-Phase 'preflight' {
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
        if (Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction SilentlyContinue) { throw 'port 5080 is in use (an Auth API already running?)' }
        if (Get-NetUDPEndpoint -LocalPort 7777 -ErrorAction SilentlyContinue) { throw 'UDP port 7777 is in use (a GameServer already running?)' }
        if (-not (Test-Path (Join-Path $repo 'unity\Library'))) { Write-Host '[qa] unity/Library is missing: the first Unity run imports the whole project (several minutes)' -ForegroundColor Yellow }
        'editor closed, ports free'
    }

    Invoke-Phase 'docker' {
        $running = [string](Invoke-Logged 'docker' @('compose', 'ps', '--status', 'running', '-q') 'docker-ps.log')
        $script:composeWasRunning = $running.Trim().Length -gt 0
        $null = Invoke-Logged 'docker' @('compose', 'up', '-d', '--wait') 'docker.log'
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
        $null = Invoke-Logged 'dotnet' @('run', '--no-build', '--project', 'src/Tools/DbMigrate', '--', '--dev') 'dbmigrate.log'
        'mmorpg_global, mmorpg_realm_1'
    }

    Invoke-Phase 'servers' {
        # Same ticket secret for the Auth API (issues tickets) and the GameServer (checks them); never written to disk.
        Set-RunEnv 'MMORPG_TICKET_SECRET' (New-Secret)
        Set-RunEnv 'MMORPG_TICKET_KEY_ID' '1'
        Set-RunEnv 'MMORPG_REALM_ID' '1'
        Set-RunEnv 'MMORPG_GAME_PORT' '7777'
        Set-RunEnv 'MMORPG_GLOBAL_DB' 'Host=127.0.0.1;Port=5432;Username=mmorpg;Password=mmorpg_dev;Database=mmorpg_global'
        Set-RunEnv 'MMORPG_REALM_DB' 'Host=127.0.0.1;Port=5432;Username=mmorpg;Password=mmorpg_dev;Database=mmorpg_realm_1'
        Set-RunEnv 'MMORPG_REDIS' '127.0.0.1:6379'
        Set-RunEnv 'MMORPG_TELEMETRY_DIR' (Join-Path $runDir 'telemetry')
        Set-RunEnv 'MMORPG_AUTH_TOKEN_SECRET' (New-Secret)
        Set-RunEnv 'MMORPG_AUTH_IDENTITY' 'dev'
        $bin = Join-Path $repo 'artifacts\bin'
        $server = Start-Server 'GameServer' (Join-Path $bin 'GameServer\debug\GameServer.dll') 'gameserver.log'
        $auth = Start-Server 'AuthApi' (Join-Path $bin 'AuthApi\debug\AuthApi.dll') 'authapi.log'
        Wait-Until 'the GameServer (UDP 7777)' 60 { [bool](Get-NetUDPEndpoint -LocalPort 7777 -ErrorAction SilentlyContinue) } $server
        Wait-Until 'the Auth API (TCP 5080)' 60 { [bool](Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction SilentlyContinue) } $auth
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
            Invoke-Unity 'UnitySmoke' @('-testPlatform', 'PlayMode', '-testFilter', 'QaSmokeScenarioTests', '-devLogin', "$DevLogin") 'smoke-results.xml' 'unity-smoke.log'
        }
    } catch { $failures++ }

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
            $null = Invoke-Logged 'docker' @('compose', 'stop') 'docker-stop.log'
            Write-Host '[qa] stopped docker compose (it was not running before)'
        } catch {
            Write-Host "[qa] docker compose stop failed: $($_.Exception.Message)" -ForegroundColor Red
        }
    }
    foreach ($name in $savedEnv.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnv[$name], 'Process') }
    # After the servers and compose: the next run may start as soon as the lock is free.
    Exit-RunLock

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
        devLogin = $DevLogin; phases = $phases; steps = $steps
    }
    $summary | ConvertTo-Json -Depth 6 | Out-File -Encoding utf8 (Join-Path $runDir 'summary.json')
    Write-Host ''
    $verdict = 'FAILED'
    if ($exitCode -eq 0) { $verdict = 'PASSED' }
    Write-Host ("[qa] {0} in {1:0} s; results in {2}" -f $verdict, $clock.Elapsed.TotalSeconds, $runDir)
}
exit $exitCode
