<#
.SYNOPSIS
    Test-only: puts a stack of one item into the first free Normal bag slot of offline dev characters (WP-M2-11).

.DESCRIPTION
    The QA smoke's split-stack step needs a stack of at least two units (spec 9.13 stack splitting), and the demon's drop
    is often a single item. This seeds one: for the character named -Character, or for every live character of the
    account of -DevLogin (the dev ticket dev:<n> is the fake SteamID n), it inserts one item_stacks row in the first free
    slot of the Normal bag, with its inventory_ops key and its audit_log add row (R-7.1-06: the ledger still accounts for
    every unit), through `docker compose exec postgres psql` on the local docker-compose server.

    Dev databases only (mmorpg_global / mmorpg_realm_1, or their _<DbSuffix> copies); never point it at anything else.
    The characters must be OFFLINE: a world that holds a character saves its own bag over this row. It goes around
    InventoryService (the only item writer in the game, rule 4) on purpose, as test data, like Set-KillCount.ps1.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/qa/Set-BagItem.ps1 -DevLogin 4711
    powershell -ExecutionPolicy Bypass -File tools/qa/Set-BagItem.ps1 -Character QaAbc -Item m1.demonHorn -Count 7 -DbSuffix wpm211
#>
param(
    # The account of a dev login (dev:<n>): every live character of it is seeded. 0: use -Character.
    [long]$DevLogin = 0,
    # A character's name (characters.name), when -DevLogin is 0.
    [string]$Character = '',
    # A stackable ItemDef id the Normal bag takes (data/tables/ItemDef.json).
    [string]$Item = 'm1.demonAsh',
    # Units in the stack (at most the item's maxStack).
    [ValidateRange(2, 999)][int]$Count = 5,
    # Own dev databases (DbMigrate --db-suffix): mmorpg_*_<suffix>. Empty: the shared ones.
    [ValidatePattern('^[a-z0-9_]{0,32}$')][string]$DbSuffix = ''
)

$ErrorActionPreference = 'Stop'
if ($DevLogin -le 0 -and $Character.Length -eq 0) { throw 'give -DevLogin or -Character' }
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$globalDb = 'mmorpg_global'
$realmDb = 'mmorpg_realm_1'
if ($DbSuffix.Length -gt 0) { $globalDb += '_' + $DbSuffix; $realmDb += '_' + $DbSuffix }

# The Normal bag's code and slot count from the committed ContainerDef row (the table has comments: no JSON parser).
$containers = Get-Content -Raw (Join-Path $repo 'data\tables\ContainerDef.json')
if ($containers -notmatch '"id":\s*"normalBag",\s*"code":\s*(\d+),\s*"slots":\s*(\d+)') { throw 'no normalBag row in ContainerDef.json' }
$bagCode = $Matches[1]
$bagSlots = $Matches[2]

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
    $account = 0
    if ($DevLogin -gt 0) {
        # psql variables (:'name') quote the values; -c would not substitute them, so the SQL goes in on stdin.
        $found = Invoke-Psql $globalDb "SELECT account_id FROM accounts WHERE steam_id = :'steam'::bigint;" @("steam=$DevLogin")
        $ids = @($found | Where-Object { $_ -match '^\d+$' })
        if ($ids.Count -eq 0) {
            Write-Host "[bag] dev:$DevLogin has no account in $globalDb yet: nothing to seed (run the smoke once first)"
            return
        }
        $account = $ids[0]
    }
    $sql = @'
WITH chars AS (
    SELECT character_id FROM characters
    WHERE deleted_at IS NULL AND (account_id = :'account'::bigint OR (:'account'::bigint = 0 AND name = :'character'))
), placed AS (
    INSERT INTO item_stacks (character_id, container, slot, def_id, rarity, stolen, count, created_at, updated_at)
    SELECT c.character_id, :'code'::smallint, free.slot, :'item', 0, false, :'count'::int, now(), now()
    FROM chars c
    CROSS JOIN LATERAL (
        SELECT g AS slot FROM generate_series(0, :'slots'::int - 1) g
        WHERE NOT EXISTS (SELECT 1 FROM item_stacks i WHERE i.character_id = c.character_id AND i.container = :'code'::smallint AND i.slot = g)
        ORDER BY g LIMIT 1) free
    RETURNING character_id, container, slot, def_id, rarity, stolen, count
), ops AS (
    INSERT INTO inventory_ops (op_key, character_id, reason, source, save_id, created_at)
    SELECT 'qa-seed:' || p.character_id || ':' || :'stamp', p.character_id, 'Admin', 'qa-seed', :'save'::uuid, now() FROM placed p
    RETURNING op_key, character_id
)
INSERT INTO audit_log (op_key, seq, character_id, kind, container, slot, def_id, rarity, stolen, count_delta, count_after, save_id, created_at)
SELECT o.op_key, 0, p.character_id, 0, p.container, p.slot, p.def_id, p.rarity, p.stolen, p.count, p.count, :'save'::uuid, now()
FROM placed p JOIN ops o ON o.character_id = p.character_id
RETURNING character_id || ' slot ' || slot;
'@
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmssfff')
    Write-Host "[bag] ${realmDb}: $Count x $Item into the Normal bag of $(if ($account -gt 0) { "dev:$DevLogin (account $account)" } else { $Character }) (offline only)"
    $rows = Invoke-Psql $realmDb $sql @("account=$account", "character=$Character", "code=$bagCode", "slots=$bagSlots", "item=$Item",
        "count=$Count", "stamp=$stamp", "save=$([guid]::NewGuid())")
    $seeded = @($rows | Where-Object { $_ -match 'slot' })
    if ($seeded.Count -eq 0) { Write-Host '[bag] no character seeded (none found, or no free slot)' }
    else { Write-Host "[bag] seeded: $($seeded -join ', ')" }
}
finally {
    Pop-Location
}
