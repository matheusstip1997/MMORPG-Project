-- Realm database, schema v23 (WP-M4-16, spec 6.5 "M4 social", 7.1; M4 decision 16): friends, mute and block.
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead.

-- One row per friendship (mutual: a request the other player accepted). Social/SocialService is its only writer. A friendship is
-- written in the save of the character that accepted and deleted in the save of the one who ended it (or blocked the other), so
-- it is one row for both sides. character_lo < character_hi. name_lo is lo's name as hi saw it when the friendship formed
-- (DisplayIdentity, rule 8), name_hi hi's name as lo saw it: each side's list shows only the name its owner saw.
CREATE TABLE friends (
    character_lo bigint      NOT NULL REFERENCES characters (character_id),
    character_hi bigint      NOT NULL REFERENCES characters (character_id),
    name_lo      text        NOT NULL,
    name_hi      text        NOT NULL,
    created_at   timestamptz NOT NULL,
    CONSTRAINT friends_pkey PRIMARY KEY (character_lo, character_hi),
    CONSTRAINT friends_ordered CHECK (character_lo < character_hi)
);

CREATE INDEX friends_hi ON friends (character_hi);

-- One row per muted or blocked player of a character, written in that character's save. kind: 1 mute (their chat lines are
-- hidden), 2 block (their lines are hidden and their whispers, party, trade and friend requests refused). target_name is the
-- target's name as the character saw it when it muted or blocked them (DisplayIdentity, rule 8).
CREATE TABLE blocks (
    character_id bigint      NOT NULL REFERENCES characters (character_id),
    target_id    bigint      NOT NULL REFERENCES characters (character_id),
    kind         smallint    NOT NULL CHECK (kind IN (1, 2)),
    target_name  text        NOT NULL,
    created_at   timestamptz NOT NULL,
    CONSTRAINT blocks_pkey PRIMARY KEY (character_id, target_id),
    CONSTRAINT blocks_not_self CHECK (character_id <> target_id)
);
