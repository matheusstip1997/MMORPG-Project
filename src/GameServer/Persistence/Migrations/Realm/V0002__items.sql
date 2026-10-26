-- Realm database, schema v2 (backlog G, spec 4.1, 7.1): item stacks, the inventory idempotency journal and the audit log.
-- Only InventoryService changes items (architecture rule 4). Its changes are written by the character save that carries
-- them (save class 2, R-7.1-01), in the same transaction as the version-checked characters update (rule 5).
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead.

-- Stackable items (spec 4.1 implementation: (defId, rarity, stolenFlag) x count). One row per occupied slot.
-- Equipment and other instances get item_instances with their own migration (M2+).
CREATE TABLE item_stacks (
    item_id      bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    character_id bigint      NOT NULL REFERENCES characters (character_id),
    -- ContainerDef.code (1 = Normal bag) and the slot inside it.
    container    smallint    NOT NULL CHECK (container >= 0),
    slot         smallint    NOT NULL CHECK (slot >= 0),
    -- ItemDef.id (stable text id) and RarityDef.tier.
    def_id       text        NOT NULL,
    rarity       smallint    NOT NULL CHECK (rarity >= 0),
    stolen       boolean     NOT NULL,
    count        integer     NOT NULL CHECK (count > 0),
    -- Versioned item properties (spec 1.3: versioned JSON for item properties), so new attributes need no
    -- risky migration. "v" is the properties schema version; stackables in M1 have no other properties.
    properties   jsonb       NOT NULL DEFAULT '{"v":1}'::jsonb CHECK (properties ? 'v'),
    created_at   timestamptz NOT NULL,
    updated_at   timestamptz NOT NULL
);

CREATE UNIQUE INDEX item_stacks_slot_key ON item_stacks (character_id, container, slot);

-- Idempotency keys of applied inventory operations (rule 5). A key is applied once, ever: a second insert fails the
-- whole save transaction, which is the database backstop against duplication.
CREATE TABLE inventory_ops (
    op_key       text        PRIMARY KEY,
    character_id bigint      NOT NULL REFERENCES characters (character_id),
    reason       text        NOT NULL,
    source       text        NOT NULL,
    save_id      uuid        NOT NULL,
    created_at   timestamptz NOT NULL
);

-- One row per slot an operation changed (R-7.1-06: items can be reconstructed from the ledger).
CREATE TABLE audit_log (
    audit_id     bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    op_key       text        NOT NULL REFERENCES inventory_ops (op_key),
    seq          smallint    NOT NULL CHECK (seq >= 0),
    character_id bigint      NOT NULL REFERENCES characters (character_id),
    -- AuditKind: 0 add, 1 remove, 2 move.
    kind         smallint    NOT NULL,
    container    smallint    NOT NULL,
    slot         smallint    NOT NULL,
    def_id       text        NOT NULL,
    rarity       smallint    NOT NULL,
    stolen       boolean     NOT NULL,
    count_delta  integer     NOT NULL,
    count_after  integer     NOT NULL CHECK (count_after >= 0),
    save_id      uuid        NOT NULL,
    created_at   timestamptz NOT NULL,
    CONSTRAINT audit_log_op_seq_key UNIQUE (op_key, seq)
);

CREATE INDEX audit_log_character ON audit_log (character_id, audit_id);
