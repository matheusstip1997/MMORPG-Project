<#
.SYNOPSIS
    Test-only: sets a character's kill count of one monster type in a dev realm database (WP-M2-8, spec 14.2).

.DESCRIPTION
    The fast way to a Bystial book milestone in a play session or a smoke run: the book reads the kill counters of
    monster_kill_counts (R-14.2-01), so a character with 99 demon kills earns the demon's normal card with its next demon
    kill (R-14.2-02; or at once at login when the count already reaches a threshold). It upserts one row of
    monster_kill_counts through `docker compose exec postgres psql` on the local docker-compose server.

    Dev databases only (mmorpg_realm_1 or mmorpg_realm_1_<DbSuffix>); never point it at anything else. The character must
    be OFFLINE: a world that holds the character saves its own count over this one (saves write absolute counts).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/qa/Set-KillCount.ps1 -Character hero1s0 -Count 99
    powershell -ExecutionPolicy Bypass -File tools/qa/Set-KillCount.ps1 -Character hero1s0 -Monster m1.cyclopsBoss -Count 4 -DbSuffix wpm28
#>
param(
    # The character's name (characters.name), as the character select screen shows it.
    [Parameter(Mandatory = $true)][string]$Character,
    # A MonsterDef id (data/tables/MonsterDef.json).
    [string]$Monster = 'm1.demon',
    # The kill count to set (absolute, at least 1).
    [ValidateRange(1, 2000000000)][long]$Count = 99,
    # Own dev databases (DbMigrate --db-suffix): mmorpg_realm_1_<suffix>. Empty: the shared mmorpg_realm_1.
    [ValidatePattern('^[a-z0-9_]{0,32}$')][string]$DbSuffix = ''
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$realmDb = 'mmorpg_realm_1'
if ($DbSuffix.Length -gt 0) { $realmDb += '_' + $DbSuffix }

# psql variables (:'name') quote the values; -c would not substitute them, so the SQL goes in on stdin.
$sql = @'
INSERT INTO monster_kill_counts (character_id, monster_def_id, count, updated_at)
SELECT character_id, :'monster', :'count'::bigint, now() FROM characters WHERE name = :'character' AND deleted_at IS NULL
ON CONFLICT (character_id, monster_def_id) DO UPDATE SET count = EXCLUDED.count, updated_at = EXCLUDED.updated_at
RETURNING character_id;
'@

Push-Location $repo
try {
    Write-Host "[kills] $Character on ${realmDb}: $Monster = $Count (the character must be offline)"
    $rows = $sql | & docker compose exec -T postgres psql -U mmorpg -d $realmDb -At -v ON_ERROR_STOP=1 `
        -v "character=$Character" -v "monster=$Monster" -v "count=$Count" -f -
    if ($LASTEXITCODE -ne 0) { throw "psql exited with $LASTEXITCODE" }
    $ids = @($rows | Where-Object { $_ -match '^\d+$' })
    if ($ids.Count -eq 0) { throw "no live character named '$Character' in $realmDb" }
    Write-Host "[kills] set for character $($ids -join ', ')"
}
finally {
    Pop-Location
}
