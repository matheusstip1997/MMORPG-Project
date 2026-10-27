-- Realm database, schema v4 (WP-M2-1, spec 4.1 M2 item model, 7.1): item instances (equipment, molds and tools), their
-- creation counter, and the instance id on audit rows. Only InventoryService changes items (architecture rule 4); its
-- changes are written by the character save that carries them (save class 2, R-7.1-01), in the same transaction as the
-- version-checked characters update (rule 5).
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead.

-- One row per instance (spec 4.1: "instances are rows with a unique ID"). The id is made by the game server without a
-- database round trip: (creator character_id << 24) | seq, where seq is the creator's item_instance_seq below. The row
-- keeps its id for life; character_id is the current owner. container and slot are where it lies (ContainerDef.code;
-- in the Equipped container the slot is EquipSlotDef.code).
CREATE TABLE item_instances (
    instance_id  bigint      PRIMARY KEY CHECK (instance_id > 0),
    character_id bigint      NOT NULL REFERENCES characters (character_id),
    container    smallint    NOT NULL CHECK (container >= 0),
    slot         smallint    NOT NULL CHECK (slot >= 0),
    -- ItemDef.id (stable text id) and RarityDef.tier.
    def_id       text        NOT NULL,
    rarity       smallint    NOT NULL CHECK (rarity >= 0),
    -- Carried, inert until M4/M5 (R-4.1-04): stolen mark and the instance's own non-tradable flag.
    stolen       boolean     NOT NULL,
    non_tradable boolean     NOT NULL,
    -- Versioned item properties (spec 1.3), "v" the schema version. v1: enchant level and gem list (inert until M3),
    -- original owner and owner history (M4).
    properties   jsonb       NOT NULL DEFAULT '{"v":1}'::jsonb CHECK (properties ? 'v'),
    created_at   timestamptz NOT NULL,
    updated_at   timestamptz NOT NULL,
    -- Deferred, so a save that swaps two instances between slots commits whatever order it writes them in.
    CONSTRAINT item_instances_slot_key UNIQUE (character_id, container, slot) DEFERRABLE INITIALLY DEFERRED
);

CREATE INDEX item_instances_character ON item_instances (character_id);

-- The last instance sequence number this character created. Advanced in memory by the game server and written in the
-- same transaction as the instances it numbered, so a crash rolls both back and no id is ever given twice.
ALTER TABLE characters ADD COLUMN item_instance_seq bigint NOT NULL DEFAULT 0 CHECK (item_instance_seq >= 0);

-- R-7.1-06: an instance's whole history in the ledger. NULL for stackables.
ALTER TABLE audit_log ADD COLUMN instance_id bigint;

CREATE INDEX audit_log_instance ON audit_log (instance_id) WHERE instance_id IS NOT NULL;
