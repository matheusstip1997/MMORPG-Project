<#
.SYNOPSIS
    Test-only: puts one instance item (a tool or a mold) into the first free slot of the Safe bag's equipment area of
    offline dev characters (WP-M3-5).

.DESCRIPTION
    The QA smoke's gather-ore step needs a pickaxe in the Safe bag's equipment area (spec 4.1 M3 item model: tools count
    there; R-5.2-02), and Set-BagItem.ps1 seeds only stacks into the Normal bag. This seeds one instance: for the character
    named -Character, or for every live character of the account of -DevLogin (the dev ticket dev:<n> is the fake SteamID
    n), it advances characters.item_instance_seq, inserts the item_instances row with the id the game server would make
    ((character_id << 24) | seq), its inventory_ops key and its audit_log add row with the instance id (R-7.1-06: the
    ledger still accounts for it), through `docker compose exec postgres psql` on the local docker-compose server. A
    character that already holds an instance of the item is left as it is, so a repeated run seeds nothing more.

    Dev databases only (mmorpg_global / mmorpg_realm_1, or their _<DbSuffix> copies); never point it at anything else.
    The characters must be OFFLINE: a world that holds a character saves its own bag over this row. It goes around
    InventoryService (the only item writer in the game, rule 4) on purpose, as test data, like Set-BagItem.ps1.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/qa/Set-BagInstance.ps1 -DevLogin 4711
    powershell -ExecutionPolicy Bypass -File tools/qa/Set-BagInstance.ps1 -Character QaAbc -Item m3.pickaxeT1 -DbSuffix wpm35
#>
param(
    # The account of a dev login (dev:<n>): every live character of it is seeded. 0: use -Character.
    [long]$DevLogin = 0,
    # A character's name (characters.name), when -DevLogin is 0.
    [string]$Character = '',
    # An instance ItemDef id the Safe bag's equipment area takes (a moldOrTool or equipment row of data/tables/ItemDef.json).
    [string]$Item = 'm3.pickaxeT1',
    # Own dev databases (DbMigrate --db-suffix): mmorpg_*_<suffix>. Empty: the shared ones.
    [ValidatePattern('^[a-z0-9_]{0,32}$')][string]$DbSuffix = ''
)

$ErrorActionPreference = 'Stop'
if ($DevLogin -le 0 -and $Character.Length -eq 0) { throw 'give -DevLogin or -Character' }
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$globalDb = 'mmorpg_global'
$realmDb = 'mmorpg_realm_1'
if ($DbSuffix.Length -gt 0) { $globalDb += '_' + $DbSuffix; $realmDb += '_' + $DbSuffix }

# The Safe bag equipment area's code and slot count from the committed ContainerDef row (the table has comments: no JSON parser).
$containers = Get-Content -Raw (Join-Path $repo 'data\tables\ContainerDef.json')
if ($containers -notmatch '"id":\s*"safeEquipment",\s*"code":\s*(\d+),\s*"slots":\s*(\d+)') { throw 'no safeEquipment row in ContainerDef.json' }
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
            Write-Host "[instance] dev:$DevLogin has no account in $globalDb yet: nothing to seed (run the smoke once first)"
            return
        }
        $account = $ids[0]
    }
    $sql = @'
WITH chars AS (
    SELECT c.character_id FROM characters c
    WHERE c.deleted_at IS NULL AND (c.account_id = :'account'::bigint OR (:'account'::bigint = 0 AND c.name = :'character'))
      AND NOT EXISTS (SELECT 1 FROM item_instances i WHERE i.character_id = c.character_id AND i.def_id = :'item')
), free AS (
    SELECT c.character_id, f.slot FROM chars c
    CROSS JOIN LATERAL (
        SELECT g AS slot FROM generate_series(0, :'slots'::int - 1) g
        WHERE NOT EXISTS (SELECT 1 FROM item_instances i WHERE i.character_id = c.character_id AND i.container = :'code'::smallint AND i.slot = g)
          AND NOT EXISTS (SELECT 1 FROM item_stacks s WHERE s.character_id = c.character_id AND s.container = :'code'::smallint AND s.slot = g)
        ORDER BY g LIMIT 1) f
), seq AS (
    UPDATE characters ch SET item_instance_seq = ch.item_instance_seq + 1
    FROM free WHERE ch.character_id = free.character_id
    RETURNING ch.character_id, free.slot, (ch.character_id << 24) | ch.item_instance_seq AS instance_id
), placed AS (
    INSERT INTO item_instances (instance_id, character_id, container, slot, def_id, rarity, stolen, non_tradable, properties,
        created_at, updated_at)
    SELECT s.instance_id, s.character_id, :'code'::smallint, s.slot, :'item', 0, false, false,
        jsonb_build_object('v', 1, 'originalOwner', s.character_id), now(), now()
    FROM seq s
    RETURNING instance_id, character_id, container, slot, def_id, rarity, stolen
), ops AS (
    INSERT INTO inventory_ops (op_key, character_id, reason, source, save_id, created_at)
    SELECT 'qa-seed:' || p.character_id || ':' || :'stamp', p.character_id, 'Admin', 'qa-seed', :'save'::uuid, now() FROM placed p
    RETURNING op_key, character_id
)
INSERT INTO audit_log (op_key, seq, character_id, kind, container, slot, def_id, rarity, stolen, count_delta, count_after, save_id,
    created_at, instance_id)
SELECT o.op_key, 0, p.character_id, 0, p.container, p.slot, p.def_id, p.rarity, p.stolen, 1, 1, :'save'::uuid, now(), p.instance_id
FROM placed p JOIN ops o ON o.character_id = p.character_id
RETURNING character_id || ' slot ' || slot || ' instance ' || instance_id;
'@
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmssfff')
    Write-Host "[instance] ${realmDb}: $Item into the Safe bag equipment area of $(if ($account -gt 0) { "dev:$DevLogin (account $account)" } else { $Character }) (offline only)"
    $rows = Invoke-Psql $realmDb $sql @("account=$account", "character=$Character", "code=$bagCode", "slots=$bagSlots", "item=$Item",
        "stamp=$stamp", "save=$([guid]::NewGuid())")
    $seeded = @($rows | Where-Object { $_ -match 'instance' })
    if ($seeded.Count -eq 0) { Write-Host '[instance] no character seeded (none found, it already holds one, or no free slot)' }
    else { Write-Host "[instance] seeded: $($seeded -join ', ')" }
}
finally {
    Pop-Location
}
