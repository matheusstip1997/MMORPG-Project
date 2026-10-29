-- Realm database, schema v11 (WP-M2-13, spec 3.4 R-3.4-08, 7.1): free attribute points. Every level from 2 gives
-- ProgressionSettings.attributePointsPerLevel unspent points, placed one at a time on STR, DEX, STA or INT (user decision
-- 2026-10-29); a skill respec gives every placed point back. ProgressionStore is the only writer of these columns.
-- The columns are written by every character save (like rank and skill points); a placement or a reset also writes its
-- attribute_ops row in the same transaction as the version-checked characters update (rule 5).
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead.

ALTER TABLE characters
    ADD COLUMN attribute_points integer NOT NULL DEFAULT 0 CHECK (attribute_points >= 0),
    ADD COLUMN str_placed       integer NOT NULL DEFAULT 0 CHECK (str_placed >= 0),
    ADD COLUMN dex_placed       integer NOT NULL DEFAULT 0 CHECK (dex_placed >= 0),
    ADD COLUMN sta_placed       integer NOT NULL DEFAULT 0 CHECK (sta_placed >= 0),
    ADD COLUMN int_placed       integer NOT NULL DEFAULT 0 CHECK (int_placed >= 0);

-- Existing characters get the points of the levels they already have, unspent (user decision 2026-10-29): one per level
-- from level 2, the value of ProgressionSettings.attributePointsPerLevel when this migration was written.
UPDATE characters SET attribute_points = GREATEST(level - 1, 0);

-- One row per placement or reset (R-3.4-08): the audit row, and with op_key the idempotency key (rule 5).
-- op 1 = place one point on attribute (0 STR, 1 DEX, 2 STA, 3 INT); op 2 = reset by a skill respec (attribute NULL, points =
-- the points given back; its op_key is the respec's skill op key with ":attributes"). placed_* hold STR, DEX, STA, INT.
CREATE TABLE attribute_ops (
    op_key        text        PRIMARY KEY,
    character_id  bigint      NOT NULL REFERENCES characters (character_id),
    op            smallint    NOT NULL CHECK (op IN (1, 2)),
    attribute     smallint    NULL CHECK (attribute BETWEEN 0 AND 3),
    points        integer     NOT NULL CHECK (points > 0),
    points_before integer     NOT NULL CHECK (points_before >= 0),
    points_after  integer     NOT NULL CHECK (points_after >= 0),
    placed_before integer[]   NOT NULL CHECK (cardinality(placed_before) = 4),
    placed_after  integer[]   NOT NULL CHECK (cardinality(placed_after) = 4),
    save_id       uuid        NOT NULL,
    created_at    timestamptz NOT NULL,
    CHECK ((op = 1) = (attribute IS NOT NULL))
);

CREATE INDEX attribute_ops_character ON attribute_ops (character_id, created_at);
