<#
.SYNOPSIS
    Test-only: learns a ground skill for offline dev mages and puts it on the hotbar (QA wave 2 F4).

.DESCRIPTION
    The QA smoke's lock-demon step checks that a ground skill aims at the mouse under a target lock (M3 decision 16), and only
    a mage of the node's minCharLevel holds one (Ice Pike, from its row in data/tables/SkillTree.json). The smoke's learn-skill
    step takes one row-0 node and cannot get there. This seeds it: for every live mage of the account of -DevLogin at that
    level or above it inserts the node and the nodes it requires into character_skills and adds -Node to the saved hotbar on
    -Slot, through `docker compose exec postgres psql` on the local docker-compose server. A character below the level gets
    nothing: the step then says n/a, why.

    Dev databases only (mmorpg_global / mmorpg_realm_1, or their _<DbSuffix> copies); never point it at anything else. The
    characters must be OFFLINE: a world that holds a character saves its own skills over these rows. It goes around
    SkillTreeService (the only writer of learned nodes) on purpose, as test data, like Set-BagItem.ps1.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/qa/Set-LearnedSkill.ps1 -DevLogin 4711 -DbSuffix fix3
#>
param(
    # The account of a dev login (dev:<n>).
    [Parameter(Mandatory)][long]$DevLogin,
    # The SkillTree node on the hotbar.
    [string]$Node = 'mage.icePike',
    # The nodes it requires, learned with it.
    [string[]]$Requires = @('mage.iceShard'),
    # The hotbar slot (the kits use Q E R F and 1, spec 9.6, so 2 is free).
    [ValidateRange(1, 11)][int]$Slot = 2,
    # Own dev databases (DbMigrate --db-suffix): mmorpg_*_<suffix>. Empty: the shared ones.
    [ValidatePattern('^[a-z0-9_]{0,32}$')][string]$DbSuffix = ''
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$globalDb = 'mmorpg_global'
$realmDb = 'mmorpg_realm_1'
if ($DbSuffix.Length -gt 0) { $globalDb += '_' + $DbSuffix; $realmDb += '_' + $DbSuffix }

# The node's class and level from the committed SkillTree row (the table has comments: no JSON parser).
$tree = Get-Content -Raw (Join-Path $repo 'data\tables\SkillTree.json')
if ($tree -notmatch ('"id":\s*"' + [regex]::Escape($Node) + '".*?"classId":\s*"(\w+)",\s*"minCharLevel":\s*(\d+)')) { throw "no SkillTree row $Node" }
$class = $Matches[1]
$level = $Matches[2]

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
        Write-Host "[skill] dev:$DevLogin has no account in $globalDb yet: nothing to seed (run the smoke once first)"
        return
    }
    $sql = @'
WITH chars AS (
    SELECT character_id FROM characters
    WHERE deleted_at IS NULL AND account_id = :'account'::bigint AND class_id = :'class' AND level >= :'level'::int
), learned AS (
    INSERT INTO character_skills (character_id, node_id, level, updated_at)
    SELECT c.character_id, n, 1, now() FROM chars c, unnest(string_to_array(:'nodes', ',')) AS n
    ON CONFLICT DO NOTHING
)
UPDATE characters SET
    hotbar = jsonb_set(COALESCE(hotbar, '{"v":1,"slots":{}}'::jsonb), ARRAY['slots', :'slot'], to_jsonb(:'node'::text), true),
    version = version + 1
WHERE character_id IN (SELECT character_id FROM chars)
RETURNING 'character ' || character_id;
'@
    $nodes = (@($Node) + $Requires) -join ','
    $rows = Invoke-Psql $realmDb $sql @("account=$($ids[0])", "class=$class", "level=$level", "nodes=$nodes", "slot=$Slot", "node=$Node")
    $seeded = @($rows | Where-Object { $_ -match 'character' })
    if ($seeded.Count -eq 0) { Write-Host "[skill] no $class of level $level or above on dev:${DevLogin}: nothing seeded" }
    else { Write-Host "[skill] $Node on slot $Slot of: $($seeded -join ', ')" }
}
finally {
    Pop-Location
}
