-- Realm database, schema v18 (WP-M4-5, spec 7.1, 7.2 R-7.2-02, 7.4): economy snapshots and the dashboard's reads.
-- V0016 and V0017 belong to parallel M4 work (WP-M4-3 mail, WP-M4-4 escrow); MigrationRunner applies every known version
-- that is not applied yet, in ascending order, so a gap is fine. The number is a proposal, assigned at merge.
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead.

-- One row per economy snapshot a world takes after a demand control step (EconomySettings.snapshotIntervalMs): the gold
-- created and destroyed since its previous row (of the same world run), by reason, the gold the online characters held and
-- the active players. Written by a background worker, never on the tick (spec 1.6). A snapshot moves no value: it is a
-- periodic record like the demand state, not an audit row; gold_ledger stays the authoritative record of every change.
CREATE TABLE faucet_sink_snapshots (
    snapshot_id    bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    world_id       integer     NOT NULL,
    -- The world's IClock time after the control step.
    taken_at       timestamptz NOT NULL,
    -- The previous row of this world run, or the time the world started.
    period_start   timestamptz NOT NULL,
    gold_created   bigint      NOT NULL CHECK (gold_created >= 0),
    gold_destroyed bigint      NOT NULL CHECK (gold_destroyed >= 0),
    gold_online    bigint      NOT NULL CHECK (gold_online >= 0),
    active_players integer     NOT NULL CHECK (active_players >= 0),
    -- {"<inventoryReason camelCase>": {"created": n, "destroyed": n, "itemsDestroyed": n}}: the reasons that moved in the
    -- period only. Names, not numbers, like gold_ledger.reason.
    by_reason      jsonb       NOT NULL,
    CHECK (period_start <= taken_at),
    -- A retried insert after an unknown outcome is a no-op.
    CONSTRAINT faucet_sink_snapshots_world_time UNIQUE (world_id, taken_at)
);

CREATE INDEX faucet_sink_snapshots_time ON faucet_sink_snapshots (taken_at);

-- The dashboard's faucet and sink totals per UTC hour and by reason read the ledger by time.
CREATE INDEX gold_ledger_created_reason ON gold_ledger (created_at, reason);

-- The admin panel records every economy view (spec 7.4: every admin action is logged). WP-M4-3 adds an action kind too:
-- whichever of the two migrations merges later lists both kinds here.
ALTER TABLE admin_actions DROP CONSTRAINT admin_actions_action_check;
ALTER TABLE admin_actions ADD CONSTRAINT admin_actions_action_check
    CHECK (action IN ('sign_in', 'search', 'view_character', 'view_logs', 'view_telemetry', 'kick', 'view_economy'));
