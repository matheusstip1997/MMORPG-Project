<#
.SYNOPSIS
    Test-only: sends a system mail with an item and gold to the dev characters of a dev login (WP-M4-3, the claim-mail step).

.DESCRIPTION
    The QA smoke's claim-mail step claims a mail into the bag. This seeds one for every live character of the account of
    -DevLogin (the dev ticket dev:<n> is the fake SteamID n) through `DbMigrate --dev --mail`, the mail send path
    (MailWriter, an admin compensation; never raw SQL for the mail). The character names are read with
    `docker compose exec postgres psql` on the local docker-compose server. -Tag goes into the mail's operation key
    (`--mail-tag`): the same tag writes nothing more, another tag a new mail (the smoke passes its run id).

    Dev databases only (mmorpg_global / mmorpg_realm_1, or their _<DbSuffix> copies); never point it at anything else.
    DbMigrate must be built (Debug: only a Debug build has --mail).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/qa/Send-DevMail.ps1 -DevLogin 4711 -Tag 20261109-1200 -DbSuffix m4mail
#>
param(
    # The account of a dev login (dev:<n>): every live character of it gets the mail.
    [Parameter(Mandatory = $true)][long]$DevLogin,
    # Added to the mail's operation key: one mail per tag and character.
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z0-9_.-]{1,64}$')][string]$Tag,
    # A stackable ItemDef id (data/tables/ItemDef.json).
    [string]$Item = 'm1.demonAsh',
    [ValidateRange(1, 999)][int]$Count = 3,
    [ValidateRange(0, 1000000)][long]$Gold = 50,
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
    # psql variables (:'name') quote the values; -c would not substitute them, so the SQL goes in on stdin.
    $found = Invoke-Psql $globalDb "SELECT account_id FROM accounts WHERE steam_id = :'steam'::bigint;" @("steam=$DevLogin")
    $ids = @($found | Where-Object { $_ -match '^\d+$' })
    if ($ids.Count -eq 0) {
        Write-Host "[mail] dev:$DevLogin has no account in $globalDb yet: nothing to seed (run the smoke once first)"
        return
    }
    $names = @(Invoke-Psql $realmDb "SELECT name FROM characters WHERE account_id = :'account'::bigint AND deleted_at IS NULL ORDER BY character_id;" @("account=$($ids[0])") |
        Where-Object { $_ -and $_.Trim().Length -gt 0 })
    if ($names.Count -eq 0) {
        Write-Host "[mail] dev:$DevLogin has no character in $realmDb yet: nothing to seed"
        return
    }
    $migrateArgs = @('run', '--no-build', '--project', 'src/Tools/DbMigrate', '--', '--dev')
    if ($DbSuffix.Length -gt 0) { $migrateArgs += @('--db-suffix', $DbSuffix) }
    $migrateArgs += @('--mail-tag', $Tag)
    foreach ($name in $names) { $migrateArgs += @('--mail', $name.Trim(), $Item, "$Count", "$Gold") }
    & dotnet @migrateArgs
    if ($LASTEXITCODE -ne 0) { throw "DbMigrate --mail exited with $LASTEXITCODE" }
    Write-Host "[mail] dev:$DevLogin, $($names.Count) character(s): $Item x$Count and $Gold gold, tag $Tag"
}
finally {
    Pop-Location
}
