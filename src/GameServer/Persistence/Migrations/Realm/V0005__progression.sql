-- Realm database, schema v5 (WP-M2-3, spec 3.6, 7.1, 14.2): rank, skill points and achievement counters on the character,
-- the per-monster-type kill counts (R-14.2-01, shared with the Bystial book) and the rank history (audit and idempotency
-- of the rank-up, R-3.6-04). Level and exp are already in characters (V0001).
-- All of it is save class 2 (R-7.1-01): written by the character save that carries it, in the same transaction as the
-- version-checked characters update (rule 5).
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead.

-- Rank (R-3.6-01): the Rank enum value, F = 0 to SS = 7. Skill points (R-3.5-04): unspent points; one per level from level
-- 1, and nobody has spent any yet (the skill tree comes with WP-M2-5), so existing characters get their level.
-- Achievement counters (R-3.6-05): the category totals and the keys of distinct counters; "v" is the format version.
ALTER TABLE characters
    ADD COLUMN rank                 smallint NOT NULL DEFAULT 0 CHECK (rank BETWEEN 0 AND 7),
    ADD COLUMN skill_points         integer  NOT NULL DEFAULT 1 CHECK (skill_points >= 0),
    ADD COLUMN achievement_counters jsonb    NOT NULL DEFAULT '{"v":1}'::jsonb CHECK (achievement_counters ? 'v');

UPDATE characters SET skill_points = level;

-- Kills per character and monster type (R-14.2-01): MonsterDef.id. Saves write absolute counts, so a retried save is safe.
CREATE TABLE monster_kill_counts (
    character_id   bigint      NOT NULL REFERENCES characters (character_id),
    monster_def_id text        NOT NULL,
    count          bigint      NOT NULL CHECK (count > 0),
    updated_at     timestamptz NOT NULL,
    PRIMARY KEY (character_id, monster_def_id)
);

-- One row per rank-up (R-3.6-04): the audit row, and with op_key the idempotency key (rule 5). A rank step happens once
-- per character ever: a second row for the same from_rank fails the whole save.
CREATE TABLE rank_history (
    op_key       text        PRIMARY KEY,
    character_id bigint      NOT NULL REFERENCES characters (character_id),
    from_rank    smallint    NOT NULL CHECK (from_rank BETWEEN 0 AND 7),
    to_rank      smallint    NOT NULL CHECK (to_rank BETWEEN 0 AND 7),
    -- The achievement counters the requirements were checked against.
    counters     jsonb       NOT NULL,
    save_id      uuid        NOT NULL,
    created_at   timestamptz NOT NULL,
    CONSTRAINT rank_history_character_from_key UNIQUE (character_id, from_rank)
);
