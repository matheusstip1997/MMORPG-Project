-- Realm database, schema v8 (WP-M2-5, spec 3.5, 7.1, 9.6): the learned skill-tree nodes, the hotbar layout and the skill
-- operations (audit and idempotency of learn, upgrade, evolve, respec and class advance; rule 5). The unspent points stay
-- in characters.skill_points (V0005), written by the same save; class_id (V0001) changes with a class advance.
-- Learned nodes and the hotbar are written by the character save that carries them, in the same transaction as the
-- version-checked characters update (rule 5); a value operation asks for that save at once (R-7.1-05).
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead.

-- One row per learned node (R-3.5-04: the character stores (node, level) only; points spent are derived). node_id is a
-- SkillTree id. Saves write the whole set, so a retried save is safe.
CREATE TABLE character_skills (
    character_id bigint      NOT NULL REFERENCES characters (character_id),
    node_id      text        NOT NULL,
    level        smallint    NOT NULL CHECK (level > 0),
    updated_at   timestamptz NOT NULL,
    PRIMARY KEY (character_id, node_id)
);

-- The hotbar the player placed (spec 9.6): {"v":1,"slots":{"<slot>":"<node id>",...}}. NULL until the first save that has
-- one: the server then places the learned skills on the default layout (ActionBindings).
ALTER TABLE characters
    ADD COLUMN hotbar jsonb CHECK (hotbar IS NULL OR hotbar ? 'v');

-- One row per committed skill operation: the audit row (who, what, points before and after) and with op_key the
-- idempotency key. op: 1 learn, 2 upgrade, 3 evolve, 4 respec, 5 advance (SkillTreeOp). A class advance happens once per
-- tier and character (R-3.5-01: the class is permanent): a second advance into the same tier fails the whole save.
CREATE TABLE skill_ops (
    op_key        text        PRIMARY KEY,
    character_id  bigint      NOT NULL REFERENCES characters (character_id),
    op            smallint    NOT NULL CHECK (op BETWEEN 1 AND 5),
    node_id       text        NOT NULL DEFAULT '',
    -- Evolve: the node it replaced (its points are lost, R-3.5-04).
    from_node_id  text        NOT NULL DEFAULT '',
    from_level    smallint    NOT NULL DEFAULT 0,
    to_level      smallint    NOT NULL DEFAULT 0,
    points_before integer     NOT NULL CHECK (points_before >= 0),
    points_after  integer     NOT NULL CHECK (points_after >= 0),
    class_from    text        NOT NULL,
    class_to      text        NOT NULL,
    -- Advance: the tier of class_to; NULL for every other op.
    advance_tier  smallint    CHECK (advance_tier IS NULL OR advance_tier BETWEEN 1 AND 3),
    save_id       uuid        NOT NULL,
    created_at    timestamptz NOT NULL
);

CREATE INDEX skill_ops_character ON skill_ops (character_id, created_at);
CREATE UNIQUE INDEX skill_ops_advance_tier ON skill_ops (character_id, advance_tier) WHERE advance_tier IS NOT NULL;

-- Skills became learned (user decision 2026-10-27, WP-M2-5): existing characters keep the fixed M1 kit of their root class
-- (spec 3.5 backlog E: the ActionBindings rows) as learned level-1 nodes, for free; their points are unchanged. The node ids
-- are the SkillTree ids of the kit skills (data/tables/SkillDef.json).
INSERT INTO character_skills (character_id, node_id, level, updated_at)
SELECT c.character_id, k.node_id, 1, now()
FROM characters c
JOIN (VALUES
    ('warrior', 'warrior.staminaBuff'), ('warrior', 'warrior.slash'), ('warrior', 'warrior.headCut'),
    ('warrior', 'warrior.piercingAttack'), ('warrior', 'warrior.jumpStrike'),
    ('archer', 'archer.dash'), ('archer', 'archer.piercingShot'), ('archer', 'archer.multiShot'),
    ('archer', 'archer.heavyShot'), ('archer', 'archer.backStep'),
    ('mage', 'mage.magicBarrier'), ('mage', 'mage.fireSpark'), ('mage', 'mage.iceShard'),
    ('mage', 'mage.windCut'), ('mage', 'mage.flamethrower'),
    ('assist', 'assist.lifeRegeneartion'), ('assist', 'assist.holyStrike'), ('assist', 'assist.healWave'),
    ('assist', 'assist.flash'), ('assist', 'assist.simpleCleanse')
) AS k (class_id, node_id) ON k.class_id = c.class_id;
