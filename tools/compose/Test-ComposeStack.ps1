<#
.SYNOPSIS
    Smoke test of the composed dev stack (backlog A and J, spec 11.3 and 7.4): a dev login end to end, then the admin
    panel finds a character, kicks it and shows its audit row, telemetry and logs.

.DESCRIPTION
    1. Optionally `docker compose up -d --wait` (-Start).
    2. Login end to end: `BotClient run` with 2 bots against the composed Auth API and GameServer. The bots run the
       Unity client's own Auth API client and connection code (src/Tools/ClientCore): dev login, create a character,
       enter the world, game connection accepted, play for a few seconds. Exit code 0 required.
    3. Admin panel: sign in (dev identity), start 1 bot in the background, search its character by account until a game
       server holds it, kick it twice with the same request id (double submit: one action), wait for the outcome
       Kicked, check that the bot was disconnected by the server, that exactly one kick row exists for the character,
       that its telemetry shows SessionEnded with the kick reason, and that the game server's log is readable.
    Needs the BotClient built in Debug (dotnet build src/Tools/BotClient). Runs on Windows PowerShell 5.1 and
    PowerShell 7 (CI). Exit code 0 only when every check passed.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/compose/Test-ComposeStack.ps1 -Start
    pwsh tools/compose/Test-ComposeStack.ps1 -AuthUrl http://127.0.0.1:15080 -Game 127.0.0.1:17777 -AdminUrl http://127.0.0.1:15090
#>
param(
    [string]$AuthUrl = 'http://127.0.0.1:5080',
    [string]$Game = '127.0.0.1:7777',
    [string]$AdminUrl = 'http://127.0.0.1:5090',
    # Run `docker compose up -d --wait` first.
    [switch]$Start,
    [string]$BotClient = '',
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ($BotClient.Length -eq 0) { $BotClient = Join-Path $repo 'artifacts/bin/BotClient/debug/BotClient.dll' }
if (-not (Test-Path $BotClient)) { throw "BotClient not found at $BotClient; run: dotnet build src/Tools/BotClient" }
$work = Join-Path ([IO.Path]::GetTempPath()) ("compose-smoke-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
$failures = New-Object System.Collections.Generic.List[string]
$headers = @{ 'X-Admin-Request' = '1' }

function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) { Write-Host "[smoke] PASS $Name" -ForegroundColor Green }
    else {
        Write-Host "[smoke] FAIL $Name $Detail" -ForegroundColor Red
        $failures.Add($Name)
    }
}

function Wait-Until([scriptblock]$Condition, [int]$Seconds = $TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $value = & $Condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Admin([string]$Method, [string]$Path, $Body = $null) {
    $arguments = @{ Method = $Method; Uri = "$AdminUrl$Path"; WebSession = $script:session; Headers = $headers; UseBasicParsing = $true }
    if ($null -ne $Body) {
        $arguments.Body = ($Body | ConvertTo-Json -Compress)
        $arguments.ContentType = 'application/json'
    }
    return Invoke-RestMethod @arguments
}

# Each run uses fresh dev accounts, so the characters are new.
$base = [long](9100000000 + (Get-Random -Minimum 0 -Maximum 900000) * 1000)

try {
    if ($Start) {
        Write-Host '[smoke] docker compose up -d --wait'
        & docker compose up -d --wait
        if ($LASTEXITCODE -ne 0) { throw 'docker compose up failed' }
    }

    # ---- 2. Login end to end ----
    Write-Host "[smoke] BotClient run: 2 bots, accounts dev:$base+"
    & dotnet $BotClient run --auth-url $AuthUrl --game $Game --bots 2 --duration-ms 15000 --account-base $base
    Check 'login end to end (2 bots entered the world and played)' ($LASTEXITCODE -eq 0) "BotClient exit code $LASTEXITCODE"

    # ---- 3. Admin panel ----
    $signIn = Invoke-RestMethod -Method Post -Uri "$AdminUrl/api/session" -SessionVariable session -Headers $headers `
        -ContentType 'application/json' -Body '{"name":"smoke"}' -UseBasicParsing
    $script:session = $session
    Check 'admin sign-in' ($signIn.ok -eq $true -and $signIn.admin -eq 'dev:smoke') ($signIn | ConvertTo-Json -Compress)

    $kickBase = $base + 500
    $botOut = Join-Path $work 'bot.out.txt'
    $botErr = Join-Path $work 'bot.err.txt'
    $bot = Start-Process -FilePath 'dotnet' -PassThru -NoNewWindow -RedirectStandardOutput $botOut -RedirectStandardError $botErr `
        -ArgumentList @($BotClient, 'run', '--auth-url', $AuthUrl, '--game', $Game, '--bots', '1', '--duration-ms', '30000',
            '--account-base', "$kickBase")

    $character = Wait-Until {
        $found = Admin 'Get' "/api/characters?account=dev:$kickBase"
        $found.characters | Where-Object { $_.loaded } | Select-Object -First 1
    }
    Check 'search by account finds the bot character held by a game server' ($null -ne $character)
    if ($null -eq $character) { throw 'no character to kick' }
    $id = $character.characterId
    Write-Host "[smoke] character $id ($($character.name)) on server $($character.server)"
    $byName = Admin 'Get' "/api/characters?name=$([uri]::EscapeDataString($character.name))"
    Check 'search by name finds it' ((@($byName.characters | Where-Object { $_.characterId -eq $id })).Count -eq 1)

    $requestId = [guid]::NewGuid().ToString()
    $first = Admin 'Post' "/api/characters/$id/kick" @{ requestId = $requestId; note = 'compose smoke' }
    $second = Admin 'Post' "/api/characters/$id/kick" @{ requestId = $requestId; note = 'compose smoke' }
    Check 'double submit is one action' ($first.action.actionId -eq $second.action.actionId -and $second.submit -eq 'AlreadyRecorded') `
        "$($first.action.actionId) / $($second.action.actionId) / $($second.submit)"

    $done = Wait-Until {
        $action = (Admin 'Get' "/api/actions/$requestId").action
        if ($action.state -eq 'done') { $action }
    } 30
    Check 'the game server ran the kick (outcome Kicked)' ($null -ne $done -and $done.outcome -eq 'Kicked') ($done | ConvertTo-Json -Compress)

    # The bot run lasts its 30 s whatever happens, then reports each bot, the kicked one as LOST.
    $exited = $bot.WaitForExit(60000)
    $botLog = if (Test-Path $botOut) { Get-Content $botOut -Raw } else { '' }
    # The client code reports a server kick as ClosedByServer (it reads the reason code only on refused connects); the
    # Kicked reason itself is checked in the telemetry below.
    Check 'the kicked client was disconnected by the server' ($exited -and $botLog -match 'LOST (ClosedByServer|.*Kicked)') ($botLog -split "`n" | Select-Object -Last 3)

    $history = Admin 'Get' "/api/actions?characterId=$id"
    $kicks = @($history.actions | Where-Object { $_.action -eq 'kick' })
    Check 'exactly one kick row in the audit log, with who and what' ($kicks.Count -eq 1 -and $kicks[0].adminName -eq 'dev:smoke' -and $kicks[0].detail -eq 'compose smoke') `
        ($kicks | ConvertTo-Json -Compress)

    $ended = Wait-Until {
        $records = (Admin 'Get' "/api/telemetry?characterId=$id").records
        $records | Where-Object { $_.k -eq 'SessionEnded' -and $_.reason -eq 6 } | Select-Object -First 1
    } 30
    Check 'telemetry shows the session ended by the kick' ($null -ne $ended)

    $logs = Admin 'Get' '/api/logs?file=gameserver.log'
    Check 'the game server log is readable' ($logs.file -eq 'gameserver.log' -and @($logs.lines).Count -gt 0) ($logs.files -join ', ')
}
catch {
    Check 'smoke run' $false $_.Exception.Message
}
finally {
    if ($null -ne $bot -and -not $bot.HasExited) { Stop-Process -Id $bot.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host "[smoke] $($failures.Count) check(s) failed: $($failures -join '; ')" -ForegroundColor Red
    exit 1
}
Write-Host '[smoke] all checks passed' -ForegroundColor Green
exit 0
