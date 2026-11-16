-- Realm database, schema v25 (WP-M4-10, spec 5.4, 7.1, 8.2; M4 decisions 10 and 11, user answers 2026-11-14 and 2026-11-15):
-- contracts and the portfolio. Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead. The number is
-- final at merge; MigrationRunner applies every known unapplied version in ascending order.

-- One row per contract. ContractService is its only writer. The customer's gold and the units of its slots are held in escrow
-- (escrow_id, holder kind 4 contract) from the proposal on (user answer 2026-11-15: the hold is at the proposal, in the customer's
-- own save, so the crafter's acceptance never writes the customer). Writers:
--   the proposal: this row in the customer's character save with the escrow hold;
--   the acceptance (crafter_id, accepted_at, deadline_at), the first forge start (work_started), the completion and the crafter's
--     cancel: guarded updates in the crafter's character save;
--   the customer's cancel and the expiry: the transaction that releases the escrow to the customer's mail (IEscrowHolderSink).
-- contract_id is assigned by the world (one world per realm in M4; TODO(M7) a shared sequence when worlds shard). kind is ForgeKind
-- (1 mold, 2 recipe), def_net_id the MoldDef or RecipeDef net id; terms is the slot list as JSON ([{supplier, itemNetId, rarity,
-- count}], supplier 0 customer, 1 crafter). output_category (ItemCategory of the output) and job_id are copied from the
-- definition so the portfolio counter of an expiry written off the world thread needs no tables. state: 1 proposed, 2 accepted,
-- 3 completed, 4 expired, 5 cancelled, 6 failed. fee is the contract fee destroyed at the completion, cost the crafter's cancel cost.
CREATE TABLE contracts (
    contract_id     bigint      PRIMARY KEY CHECK (contract_id > 0),
    propose_op_key  text        NOT NULL,
    customer_id     bigint      NOT NULL REFERENCES characters (character_id),
    crafter_id      bigint      REFERENCES characters (character_id),
    escrow_id       text        NOT NULL,
    kind            smallint    NOT NULL CHECK (kind IN (1, 2)),
    def_net_id      integer     NOT NULL CHECK (def_net_id > 0),
    def_id          text        NOT NULL,
    job_id          text        NOT NULL,
    output_category smallint    NOT NULL CHECK (output_category >= 0),
    gold            bigint      NOT NULL CHECK (gold > 0),
    deadline_hours  smallint    NOT NULL CHECK (deadline_hours > 0),
    terms           jsonb       NOT NULL,
    proposed_at     timestamptz NOT NULL,
    open_until      timestamptz NOT NULL,
    accepted_at     timestamptz,
    deadline_at     timestamptz,
    work_started    boolean     NOT NULL DEFAULT false,
    state           smallint    NOT NULL CHECK (state BETWEEN 1 AND 6),
    closed_at       timestamptz,
    accept_op_key   text,
    close_op_key    text,
    fee             bigint      NOT NULL DEFAULT 0 CHECK (fee >= 0),
    cost            bigint      NOT NULL DEFAULT 0 CHECK (cost >= 0),
    propose_save_id uuid        NOT NULL,
    accept_save_id  uuid,
    close_save_id   uuid,
    CONSTRAINT contracts_propose_op_key UNIQUE (propose_op_key),
    CONSTRAINT contracts_accept_op_key UNIQUE (accept_op_key),
    CONSTRAINT contracts_close_op_key UNIQUE (close_op_key),
    CONSTRAINT contracts_escrow UNIQUE (escrow_id),
    CONSTRAINT contracts_closed_when_not_open CHECK ((state IN (1, 2)) = (closed_at IS NULL)),
    CONSTRAINT contracts_proposed_has_no_crafter CHECK (state <> 1 OR crafter_id IS NULL),
    CONSTRAINT contracts_accepted_has_deadline CHECK ((crafter_id IS NULL) = (accepted_at IS NULL)
        AND (accepted_at IS NULL) = (deadline_at IS NULL)),
    CONSTRAINT contracts_not_own CHECK (crafter_id IS NULL OR crafter_id <> customer_id),
    CONSTRAINT contracts_completed_by_crafter CHECK (state NOT IN (3) OR crafter_id IS NOT NULL),
    CONSTRAINT contracts_open_after_proposed CHECK (open_until > proposed_at)
);

-- The world loads the open contracts at start; the per-pair cap reads the acceptances of the current UTC day.
CREATE INDEX contracts_open ON contracts (contract_id) WHERE state IN (1, 2);
CREATE INDEX contracts_customer ON contracts (customer_id) WHERE state IN (1, 2);
CREATE INDEX contracts_crafter ON contracts (crafter_id) WHERE state = 2;
CREATE INDEX contracts_accepted_at ON contracts (accepted_at) WHERE accepted_at IS NOT NULL;

-- The portfolio counters (R-5.4-04, M4 decision 11): per crafter, job, item category (ItemCategory + 1, 0 none), rarity
-- (RarityDef.tier) and outcome (PortfolioOutcome: 1 crafted, 2 refined, 3 converted, 4 conversion failed, 5..7 enchant up, same,
-- down, 8 contract completed, 9 expired, 10 cancelled). Incremented once per operation key (portfolio_ops) in the transaction
-- of the operation that counts (the forge's save for a contract; the after-commit events of the crafter's own crafts ride its next
-- save, class 2).
CREATE TABLE portfolio_counters (
    character_id bigint   NOT NULL REFERENCES characters (character_id),
    job_id       text     NOT NULL,
    category     smallint NOT NULL CHECK (category >= 0),
    rarity       smallint NOT NULL CHECK (rarity >= 0),
    outcome      smallint NOT NULL CHECK (outcome BETWEEN 1 AND 10),
    count        bigint   NOT NULL CHECK (count > 0),
    PRIMARY KEY (character_id, job_id, category, rarity, outcome)
);

CREATE TABLE portfolio_ops (
    op_key       text        PRIMARY KEY,
    character_id bigint      NOT NULL REFERENCES characters (character_id),
    counted_at   timestamptz NOT NULL
);

-- The showcase (R-5.4-04): up to ContractSettings.showcaseMax pinned snapshots per crafter, read-only, kept when the item is lost.
-- item is the instance's properties as in item_instances (quality, crafter, crafted stats, enchant, gems) at the pin.
CREATE TABLE portfolio_showcase (
    character_id bigint      NOT NULL REFERENCES characters (character_id),
    slot         smallint    NOT NULL CHECK (slot BETWEEN 1 AND 6),
    def_id       text        NOT NULL,
    rarity       smallint    NOT NULL CHECK (rarity >= 0),
    item         jsonb       NOT NULL,
    pinned_at    timestamptz NOT NULL,
    PRIMARY KEY (character_id, slot)
);

-- The hide flag (R-5.4-04): a hidden portfolio is shown to its owner only.
CREATE TABLE portfolio_settings (
    character_id bigint  PRIMARY KEY REFERENCES characters (character_id),
    hidden       boolean NOT NULL
);

-- A forge operation of a contract names it (the recipient is the customer; its output went by mail).
ALTER TABLE craft_ops ADD COLUMN contract_id bigint;

-- The contract output reaches the customer by mail: MailKind ContractOutput (9).
ALTER TABLE mail DROP CONSTRAINT mail_kind_check, ADD CONSTRAINT mail_kind_check CHECK (kind BETWEEN 1 AND 9);

-- A contract's settlement releases its escrow with target 4 (consumed: the gold to the crafter, released_to_id; the items consumed
-- by the forge session in the same save, or mailed back to the customer with it).
ALTER TABLE escrow DROP CONSTRAINT escrow_released_to_kind_check,
    ADD CONSTRAINT escrow_released_to_kind_check CHECK (released_to_kind IN (1, 2, 3, 4));
