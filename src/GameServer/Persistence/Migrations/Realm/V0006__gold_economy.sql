-- Realm database, schema v6 (WP-M2-4, spec 4.2, 7.1): gold, the gold ledger and the demand loop's state.
-- V0004 and V0005 belong to parallel M2 work (WP-M2-1 items, WP-M2-3 exp and rank); MigrationRunner applies every known
-- version that is not applied yet, in ascending order, so a gap is fine.
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead.

-- Gold is held on the character row and written only by InventoryService's batches (architecture rule 4): the save that
-- carries a batch with gold sets the final balance in the same transaction as the version-checked row update (rule 5).
ALTER TABLE characters ADD COLUMN gold bigint NOT NULL DEFAULT 0 CHECK (gold >= 0);

-- One row per gold change (R-7.1-06: gold can be reconstructed from the ledger; spec 4.2 SinkLedger: every destruction
-- by reason). The operation key is the inventory operation's (inventory_ops), applied once ever.
CREATE TABLE gold_ledger (
    ledger_id     bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    op_key        text        NOT NULL REFERENCES inventory_ops (op_key),
    character_id  bigint      NOT NULL REFERENCES characters (character_id),
    delta         bigint      NOT NULL CHECK (delta <> 0),
    balance_after bigint      NOT NULL CHECK (balance_after >= 0),
    -- GoldFlow: 0 faucet (gold created, R-4.2-01), 1 sink (gold destroyed, R-4.2-04), 2 move (between owners, later).
    flow          smallint    NOT NULL,
    -- InventoryReason by name, like inventory_ops.reason.
    reason        text        NOT NULL,
    source        text        NOT NULL,
    save_id       uuid        NOT NULL,
    created_at    timestamptz NOT NULL,
    CONSTRAINT gold_ledger_op_key UNIQUE (op_key)
);

CREATE INDEX gold_ledger_character ON gold_ledger (character_id, ledger_id);

-- The demand loop's state (R-4.2-03, spec 4.2 "state persisted periodically"): one row per counter, written every
-- DemandSettings.persistIntervalMs (volatile class, R-7.1-01) and read when the world starts. Losing the last interval
-- in a crash only shifts prices and respawn delays a little; nothing that moves value depends on it.
CREATE TABLE demand_state (
    world_id       integer     NOT NULL,
    -- DemandKeyKind: 0 kills of a monster type in a region, 1 sales of an item.
    key_kind       smallint    NOT NULL,
    -- MonsterDef.id or ItemDef.id.
    def_id         text        NOT NULL,
    -- RegionDef.id, or '' for sales (prices are per item, not per region).
    region_id      text        NOT NULL,
    decayed_milli  bigint      NOT NULL CHECK (decayed_milli >= 0),
    spawn_percent  integer     NOT NULL,
    reward_percent integer     NOT NULL,
    updated_at     timestamptz NOT NULL,
    PRIMARY KEY (world_id, key_kind, def_id, region_id)
);
