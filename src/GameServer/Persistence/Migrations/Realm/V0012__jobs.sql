-- Realm database, schema v12 (WP-M3-1, spec 5.1, 7.1, 8.2): job progress and the job operations (audit and idempotency
-- of the permanent job pick; rule 5). A pick is written by the character save that carries it, in the same transaction as
-- the version-checked characters update and the starter items it grants (InventoryRepository); it asks for that save at
-- once (R-7.1-05). Job XP, levels and points are class 2 and ride the next batch.
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead.

-- One row per (character, job) the character holds (spec 5.1: per-job records). slot is the main (1) or secondary (2) job
-- slot the record fills; a slot is written once (user decision 2026-10-30: jobs cannot be changed once picked), so a
-- character has at most one row per slot. recent is the job's diminishing-returns window (R-5.1-04):
-- {"v":1,"recent":[[activity,subject],...]}, oldest first. Saves write the rows absolutely, so a retried save is safe.
CREATE TABLE job_progress (
    character_id bigint      NOT NULL REFERENCES characters (character_id),
    job_id       text        NOT NULL,
    slot         smallint    NOT NULL CHECK (slot BETWEEN 1 AND 2),
    level        smallint    NOT NULL CHECK (level > 0),
    xp           bigint      NOT NULL CHECK (xp >= 0),
    points       integer     NOT NULL CHECK (points >= 0),
    recent       jsonb       NOT NULL CHECK (recent ? 'v'),
    updated_at   timestamptz NOT NULL,
    PRIMARY KEY (character_id, job_id),
    CONSTRAINT job_progress_slot UNIQUE (character_id, slot)
);

-- One row per committed job operation: the audit row (who, what, at which character level) and with op_key the idempotency
-- key. op: 1 pick. A pick happens once per slot and character (the permanence rule): a second pick into the same slot fails
-- the whole save, the database backstop of the in-memory refusal JobAlreadyChosen. Support repairs go through the admin
-- tools and leave their own audit row (M3 plan risk "permanent job picks").
CREATE TABLE job_ops (
    op_key         text        PRIMARY KEY,
    character_id   bigint      NOT NULL REFERENCES characters (character_id),
    op             smallint    NOT NULL CHECK (op = 1),
    job_id         text        NOT NULL,
    slot           smallint    NOT NULL CHECK (slot BETWEEN 1 AND 2),
    level_at_pick  smallint    NOT NULL CHECK (level_at_pick > 0),
    -- The starter items the pick granted (item ids), in the inventory batch of the same save.
    starter_items  text[]      NOT NULL,
    save_id        uuid        NOT NULL,
    created_at     timestamptz NOT NULL
);

CREATE INDEX job_ops_character ON job_ops (character_id, created_at);
CREATE UNIQUE INDEX job_ops_one_pick_per_slot ON job_ops (character_id, slot) WHERE op = 1;
