-- Realm database, schema v14 (WP-M3-7, spec 5.1, 7.1, 8.2): the job skill trees. The learned nodes of each job and the
-- learn and respec operations (audit and idempotency, rule 5). A learn or a respec asks for a character save at once
-- (R-7.1-05); that save writes the job records, the learned nodes and the job_ops row in one transaction, and the player
-- is answered only after it committed. The unspent points stay in job_progress.points (V0012).
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead.

-- One row per learned node of a job the character holds (R-5.1-04; like character_skills, V0008: the character stores
-- (node, level) only, points spent are derived). node_id is a JobTree id. Saves write the whole set, so a retried save is
-- safe; a respec leaves the job with no rows.
CREATE TABLE job_nodes (
    character_id bigint      NOT NULL,
    job_id       text        NOT NULL,
    node_id      text        NOT NULL,
    level        smallint    NOT NULL CHECK (level > 0),
    updated_at   timestamptz NOT NULL,
    PRIMARY KEY (character_id, job_id, node_id),
    FOREIGN KEY (character_id, job_id) REFERENCES job_progress (character_id, job_id)
);

-- job_ops gains the job tree operations. op: 1 pick, 2 learn (one level of node_id, from_level to to_level), 3 respec (the
-- whole job tree; from_level is the number of levels cleared). points_before and points_after are the job's unspent
-- points around the operation. level_at_pick is the character level at the operation for every op.
ALTER TABLE job_ops DROP CONSTRAINT job_ops_op_check;
ALTER TABLE job_ops ADD CONSTRAINT job_ops_op_check CHECK (op BETWEEN 1 AND 3);
ALTER TABLE job_ops
    ADD COLUMN node_id       text     NOT NULL DEFAULT '',
    ADD COLUMN from_level    smallint NOT NULL DEFAULT 0,
    ADD COLUMN to_level      smallint NOT NULL DEFAULT 0,
    ADD COLUMN points_before integer  NOT NULL DEFAULT 0 CHECK (points_before >= 0),
    ADD COLUMN points_after  integer  NOT NULL DEFAULT 0 CHECK (points_after >= 0);

-- A job starts with one level's job points at its pick (user answer 2026-10-30, WP-M3-7 plan), so row 0 of its tree can be
-- learned at once. Jobs picked before this migration get that point now: 1 is JobSettings.pointsPerLevel of the time (a
-- placeholder), and only dev data exists (M3 is unreleased).
UPDATE job_progress SET points = points + 1, updated_at = now();
