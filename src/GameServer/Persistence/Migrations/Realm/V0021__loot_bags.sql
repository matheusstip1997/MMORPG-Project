-- Realm database, schema v21 (WP-M4-9, spec 4.3 R-4.3-03, 4.4 R-4.4-01, 7.1, 8.2; M4 decision 8): death loot bags and the
-- stolen mark. Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead. V0019 and V0020 belong to
-- parallel M4 work; MigrationRunner applies every known unapplied version in ascending order, so a gap is fine.

-- The stolen mark (R-4.4-01): who a stack was taken from (the loot bag's owner) and when. Part of the stack key, so units
-- of two takes never share a row. NULL for a stack that is not stolen, and for a stolen flag set before WP-M4-9.
ALTER TABLE item_stacks
    ADD COLUMN stolen_from bigint      NULL,
    ADD COLUMN stolen_at   timestamptz NULL,
    ADD CONSTRAINT item_stacks_mark_needs_stolen CHECK (stolen OR (stolen_from IS NULL AND stolen_at IS NULL));

-- The audit row carries the mark of the units it moved, so the ledger still rebuilds a character exactly (R-7.1-06).
ALTER TABLE audit_log
    ADD COLUMN stolen_from bigint      NULL,
    ADD COLUMN stolen_at   timestamptz NULL;

-- A mail can carry a marked stack (the owner of a recovered bag who had stolen before gets what does not fit by mail).
ALTER TABLE mail_items
    ADD COLUMN stolen_from bigint      NULL,
    ADD COLUMN stolen_at   timestamptz NULL,
    ADD CONSTRAINT mail_items_mark_needs_stolen CHECK (stolen OR (stolen_from IS NULL AND stolen_at IS NULL));

-- A player's death loot bag (R-4.3-03): the Normal bag that dropped at the death spot. Written by the death transaction in
-- the owner's character save (one transaction with the inventory batch that removed the units: both or neither), then
-- by the takes in each taker's save, or by the expiry job. bag_id is the death's operation key (death:<world run>:<event
-- id>, unique for ever). Times are UTC from IClock (rule 6): the owner window and the expiry run on through a restart.
-- state: 1 open, 2 emptied (the last row was taken), 3 expired (the rest destroyed, a sink).
CREATE TABLE loot_bags (
    bag_id              text        PRIMARY KEY,
    owner_character_id  bigint      NOT NULL REFERENCES characters (character_id),
    region_id           text        NOT NULL,
    x_cm                integer     NOT NULL,
    y_cm                integer     NOT NULL,
    height_cm           integer     NOT NULL,
    created_at          timestamptz NOT NULL,
    owner_window_until  timestamptz NOT NULL,
    expires_at          timestamptz NOT NULL,
    state               smallint    NOT NULL CHECK (state IN (1, 2, 3)),
    create_save_id      uuid        NOT NULL,
    closed_at           timestamptz NULL,
    CHECK ((state = 1) = (closed_at IS NULL)),
    CHECK (owner_window_until >= created_at AND expires_at >= owner_window_until)
);

CREATE INDEX loot_bags_open ON loot_bags (expires_at) WHERE state = 1;
CREATE INDEX loot_bags_owner ON loot_bags (owner_character_id, state);

-- The stacks still in a bag (the Normal bag holds stacks only, R-4.1-02): idx is the row's place in the bag (the Normal
-- bag's slot order at the death). A take lowers count or deletes the row, guarded by the count the world saw.
CREATE TABLE loot_bag_items (
    bag_id       text        NOT NULL REFERENCES loot_bags (bag_id),
    idx          smallint    NOT NULL CHECK (idx >= 0),
    def_id       text        NOT NULL,
    rarity       smallint    NOT NULL CHECK (rarity >= 0),
    count        integer     NOT NULL CHECK (count > 0),
    stolen       boolean     NOT NULL,
    stolen_from  bigint      NULL,
    stolen_at    timestamptz NULL,
    PRIMARY KEY (bag_id, idx),
    CONSTRAINT loot_bag_items_mark_needs_stolen CHECK (stolen OR (stolen_from IS NULL AND stolen_at IS NULL))
);

-- Every drop, take and expiry with what it moved (rule 5: the operation key is the idempotency key, an operation is
-- applied once ever). With the inventory audit and mail_ops it rebuilds a bag (R-7.1-06).
-- kind: 1 drop, 2 take, 3 expire. outcome (a take): 1 the owner's, 2 a party member's for the owner (by mail), 3 stolen.
-- character_id: the owner (drop, expiry) or the taker. items: [{"i": idx, "d": defId, "r": rarity, "c": count}].
CREATE TABLE loot_bag_ops (
    op_key        text        PRIMARY KEY,
    kind          smallint    NOT NULL CHECK (kind BETWEEN 1 AND 3),
    bag_id        text        NOT NULL REFERENCES loot_bags (bag_id),
    character_id  bigint      NOT NULL,
    outcome       smallint    NOT NULL DEFAULT 0 CHECK (outcome BETWEEN 0 AND 3),
    items         jsonb       NOT NULL DEFAULT '[]'::jsonb,
    save_id       uuid        NULL,
    created_at    timestamptz NOT NULL,
    CHECK ((kind = 2) = (outcome > 0))
);

CREATE INDEX loot_bag_ops_bag ON loot_bag_ops (bag_id, created_at);
