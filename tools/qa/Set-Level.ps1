<#
.SYNOPSIS
    Test-only: raises the offline dev characters of a dev login to a level (WP-M4-11), for the smoke's B run.

.DESCRIPTION
    The M4 features unlock by level on the client (M4 decision 14, spec 12.3: party 12, trade and contracts 20, auction 22,
    stalls 25), so the seeded smoke plays its pair steps at level 25. This finds the live characters of the account of
    -DevLogin (a read through `docker compose exec postgres psql`) and sets each one below -Level to it with
    `DbMigrate --dev --set-level` (the dev tool's own write: exp 0, the skill and attribute points of the levels gained).
    A character at or above -Level is left as it is, so a rerun changes nothing. Before the smoke's first run the account
    has no character yet: nothing to seed (the A run creates it).

    Dev databases only (mmorpg_global / mmorpg_realm_1, or their _<DbSuffix> copies); never point it at anything else. The
    characters must be OFFLINE: DbMigrate bumps the version, so a world holding one would fail its next save.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/qa/Set-Level.ps1 -DevLogin 4711 -Level 25 -DbSuffix wp411
#>
param(
    # The account of a dev login (dev:<n>).
    [Parameter(Mandatory)][long]$DevLogin,
    # The level to raise to (at most ProgressionSettings.maxLevel; DbMigrate checks it).
    [Parameter(Mandatory)][ValidateRange(1, 1000)][int]$Level,
    # Own dev databases (DbMigrate --db-suffix): mmorpg_*_<suffix>. Empty: the shared ones.
    [ValidatePattern('^[a-z0-9_]{0,32}$')][string]$DbSuffix = ''
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$globalDb = 'mmorpg_global'
$realmDb = 'mmorpg_realm_1'
if ($DbSuffix.Length -gt 0) { $globalDb += '_' + $DbSuffix; $realmDb += '_' + $DbSuffix }

function Invoke-Psql([string]$Database, [string]$Sql, [string[]]$Variables) {
    $psqlArgs = @('compose', 'exec', '-T', 'postgres', 'psql', '-U', 'mmorpg', '-d', $Database, '-At', '-v', 'ON_ERROR_STOP=1')
    foreach ($v in $Variables) { $psqlArgs += @('-v', $v) }
    $psqlArgs += @('-f', '-')
    $out = $Sql | & docker @psqlArgs
    if ($LASTEXITCODE -ne 0) { throw "psql on $Database exited with $LASTEXITCODE" }
    return @($out)
}

Push-Location $repo
try {
    $found = Invoke-Psql $globalDb "SELECT account_id FROM accounts WHERE steam_id = :'steam'::bigint;" @("steam=$DevLogin")
    $ids = @($found | Where-Object { $_ -match '^\d+$' })
    if ($ids.Count -eq 0) {
        Write-Host "[level] dev:$DevLogin has no account in $globalDb yet: nothing to seed (run the smoke once first)"
        return
    }
    $names = Invoke-Psql $realmDb ("SELECT name FROM characters WHERE deleted_at IS NULL AND account_id = :'account'::bigint " +
        "AND level < :'level'::int ORDER BY character_id;") @("account=$($ids[0])", "level=$Level")
    $names = @($names | Where-Object { $_.Length -gt 0 })
    if ($names.Count -eq 0) {
        Write-Host "[level] no character of dev:$DevLogin below level ${Level}: nothing to seed"
        return
    }
    foreach ($name in $names) {
        $migrateArgs = @('run', '--project', 'src/Tools/DbMigrate', '--', '--dev')
        if ($DbSuffix.Length -gt 0) { $migrateArgs += @('--db-suffix', $DbSuffix) }
        $migrateArgs += @('--set-level', $name, "$Level")
        & dotnet @migrateArgs
        if ($LASTEXITCODE -ne 0) { throw "DbMigrate --set-level $name $Level exited with $LASTEXITCODE" }
        Write-Host "[level] $name is now level $Level"
    }
}
finally {
    Pop-Location
}
