-- Realm database, schema v9 (WP-M2-8, spec 14.2, 7.1): the Bystial book. Cards are progression state, never items
-- (R-14.2-03): one row per character and monster type with a card, its tier and the book slot it is placed in. The kill
-- counts it reads are monster_kill_counts (V0005, R-14.2-01); nothing here counts kills.
-- All of it is save class 2 (R-7.1-01): written by the character save that carries it, in the same transaction as the
-- version-checked characters update (rule 5).
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead.

-- tier: the BystialTier value (normal 1 to legendary 4); slot: the book slot, NULL while the card is in the collection.
-- Saves write absolute rows, so a retried save is safe.
CREATE TABLE bystial_book (
    character_id   bigint      NOT NULL REFERENCES characters (character_id),
    monster_def_id text        NOT NULL,
    tier           smallint    NOT NULL CHECK (tier BETWEEN 1 AND 4),
    slot           smallint    NULL CHECK (slot BETWEEN 0 AND 15),
    earned_at      timestamptz NOT NULL,
    updated_at     timestamptz NOT NULL,
    PRIMARY KEY (character_id, monster_def_id)
);

-- One card per slot (R-14.2-04: one card per monster type is the primary key).
CREATE UNIQUE INDEX bystial_book_slot_key ON bystial_book (character_id, slot) WHERE slot IS NOT NULL;

-- One row per book operation: the audit row, and with op_key the idempotency key (rule 5). kind: 1 earn, 2 evolve,
-- 3 place, 4 swap, 5 take out. An earn or evolve key names the character, the monster and the tier, so a tier is
-- reached once ever; a placement key names the request (world run, entity, connection, request id).
CREATE TABLE bystial_book_ops (
    op_key                text        PRIMARY KEY,
    character_id          bigint      NOT NULL REFERENCES characters (character_id),
    kind                  smallint    NOT NULL CHECK (kind BETWEEN 1 AND 5),
    monster_def_id        text        NOT NULL,
    -- The card a swap took out of the slot.
    other_monster_def_id  text        NULL,
    from_tier             smallint    NOT NULL CHECK (from_tier BETWEEN 0 AND 4),
    to_tier               smallint    NOT NULL CHECK (to_tier BETWEEN 0 AND 4),
    slot                  smallint    NULL CHECK (slot BETWEEN 0 AND 15),
    -- The kill count an earn or evolve was reached at.
    kill_count            bigint      NULL,
    save_id               uuid        NOT NULL,
    created_at            timestamptz NOT NULL
);

CREATE INDEX bystial_book_ops_character ON bystial_book_ops (character_id, created_at);
