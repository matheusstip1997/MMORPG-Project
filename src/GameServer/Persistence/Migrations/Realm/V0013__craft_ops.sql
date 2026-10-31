-- Realm database, schema v13 (WP-M3-6, spec 5.3, 5.2, 7.1, 8.2): the forge operations (audit and idempotency of every
-- craft, refine, tier conversion and mold recipe; rule 5). An operation is written by the character save that carries it,
-- in the same transaction as the version-checked characters update and the item changes it made (InventoryRepository:
-- the reserved inputs consumed, the output created); the forge asks for that save at once (R-7.1-05) and answers only
-- after it committed. Enchanting (WP-M3-8) and gem socketing (WP-M3-9) add their kinds here.
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead.

-- One row per committed operation. kind: 1 craft (a mold), 2 refine, 3 convert, 4 mold (a mold recipe); enchant and socket
-- come later. def_id is the MoldDef or RecipeDef id, tier its difficulty tier (a conversion's input tier, so the daily cap
-- of R-5.2-04 counts per output tier = tier + 1). inputs lists the consumed units as "<itemId>:<rarity>:<count>".
-- output_instance_id is the new instance (0 for a stack or none). success is false only for a failed conversion (the
-- inputs are a sink, R-5.2-04), inputs_kept true when the job tree's keep roll kept them. seed is the logged seed of every random number of the operation (rule 6). created_at is
-- the operation's time from the world clock (IClock), which the conversion daily cap counts from UTC midnight.
CREATE TABLE craft_ops (
    op_key             text        PRIMARY KEY,
    character_id       bigint      NOT NULL REFERENCES characters (character_id),
    kind               smallint    NOT NULL CHECK (kind BETWEEN 1 AND 4),
    def_id             text        NOT NULL,
    tier               smallint    NOT NULL CHECK (tier > 0),
    inputs             text[]      NOT NULL,
    output_item_id     text        NOT NULL,
    output_count       integer     NOT NULL CHECK (output_count >= 0),
    output_instance_id bigint      NOT NULL CHECK (output_instance_id >= 0),
    quality            integer     NOT NULL CHECK (quality BETWEEN 0 AND 65535),
    rarity             smallint    NOT NULL CHECK (rarity >= 0),
    success            boolean     NOT NULL,
    inputs_kept        boolean     NOT NULL,
    seed               bigint      NOT NULL,
    recipient_id       bigint      NOT NULL,
    save_id            uuid        NOT NULL,
    created_at         timestamptz NOT NULL
);

-- The conversion daily cap reads a character's conversions of the last days at login.
CREATE INDEX craft_ops_character_kind ON craft_ops (character_id, kind, created_at);
