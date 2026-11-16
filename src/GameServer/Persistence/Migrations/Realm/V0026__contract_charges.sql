-- Realm database, schema v26 (WP-M4-10 follow-up, spec 5.4; user answers 2026-11-16): the crafter's charge when a started
-- contract expires. Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead. The number is final at merge.

-- One row per contract that expired after its crafter started work ("Yes, charge on expiry"): the crafter owes
-- ContractSettings.cancelCostPercent of the contract gold. The row is written in the expiry's own transaction (the escrow's
-- release to the customer's mail with the contract's close, IEscrowHolderSink), so the obligation commits with the close and
-- never twice (charge_key is the expiry's release key plus ':charge'). The gold is taken later, in the crafter's own save, by
-- ContractService through InventoryService (reason ContractCancel, a sink): at once when the crafter is online, else at its next
-- login (user answer 2026-11-16). A crafter holding less pays what it holds; the rest is dropped (user answer 2026-11-16), and
-- collected_amount < amount records the shortfall. state: 1 pending, 2 collected.
CREATE TABLE contract_charges (
    charge_key       text        PRIMARY KEY,
    contract_id      bigint      NOT NULL REFERENCES contracts (contract_id),
    crafter_id       bigint      NOT NULL REFERENCES characters (character_id),
    amount           bigint      NOT NULL CHECK (amount > 0),
    state            smallint    NOT NULL CHECK (state IN (1, 2)),
    created_at       timestamptz NOT NULL,
    collected_amount bigint      NOT NULL DEFAULT 0 CHECK (collected_amount >= 0 AND collected_amount <= amount),
    collected_at     timestamptz,
    collect_save_id  uuid,
    CONSTRAINT contract_charges_collected CHECK ((state = 2) = (collected_at IS NOT NULL)),
    CONSTRAINT contract_charges_one_per_contract UNIQUE (contract_id)
);

-- A login reads the crafter's pending charges.
CREATE INDEX contract_charges_pending ON contract_charges (crafter_id) WHERE state = 1;
