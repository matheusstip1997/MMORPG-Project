-- Realm database, schema v17 (WP-M4-4, spec 4.5, 7.1, 8.2; M4 decision 5): transfers between characters and escrow.
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead. V0016 belongs to parallel M4 work;
-- MigrationRunner applies every known unapplied version in ascending order, so a gap is fine.

-- One row per committed transfer between two characters (a trade; later a listing sale, a stall sale, a contract
-- settlement). It is written by CharacterRepository.SaveTransferAsync in the one transaction that also writes both
-- characters' version-checked rows and pending batches (the barrier by inclusion over two characters, R-7.1-02), so the
-- two halves commit together or not at all. op_key is the operation's idempotency key (rule 5); save_id_a and save_id_b
-- are the two character saves it rode on (a retry after an unknown outcome is recognised by them). Each character's own
-- inventory_ops, audit_log and gold_ledger rows still rebuild that character alone (R-7.1-06); this row links the
-- halves. kind: 1 trade. summary: the items and gold moved each way (JSON, for admin tools and audits).
CREATE TABLE transfer_ops (
    op_key       text        PRIMARY KEY,
    kind         smallint    NOT NULL CHECK (kind > 0),
    character_a  bigint      NOT NULL REFERENCES characters (character_id),
    character_b  bigint      NOT NULL REFERENCES characters (character_id),
    save_id_a    uuid        NOT NULL,
    save_id_b    uuid        NOT NULL,
    summary      jsonb       NOT NULL,
    created_at   timestamptz NOT NULL,
    CHECK (character_a < character_b)
);

CREATE INDEX transfer_ops_character_a ON transfer_ops (character_a, created_at);
CREATE INDEX transfer_ops_character_b ON transfer_ops (character_b, created_at);

-- Escrow (spec 4.5, 5.4): assets held outside every character, by EscrowService only (the only holder of assets outside a
-- character). A hold removes the items and gold from the owner's bag in the owner's character save, in the same
-- transaction as this row (reason EscrowHold); a release gives them to a character (reason EscrowRelease, that
-- character's save) or to a mail (one transaction with the mail row). escrow_id is the hold's operation key. holder_kind:
-- 1 test, 2 listing, 3 stall, 4 contract (what holds it; holder_id its id as text). state: 1 held, 2 released.
-- released_to_kind: 1 a character, 2 a mail; release_op_key is unique, so a release is applied once ever.
CREATE TABLE escrow (
    escrow_id           text        PRIMARY KEY,
    owner_character_id  bigint      NOT NULL REFERENCES characters (character_id),
    holder_kind         smallint    NOT NULL CHECK (holder_kind > 0),
    holder_id           text        NOT NULL,
    gold                bigint      NOT NULL CHECK (gold >= 0),
    state               smallint    NOT NULL CHECK (state IN (1, 2)),
    created_at          timestamptz NOT NULL,
    hold_save_id        uuid        NOT NULL,
    released_at         timestamptz,
    released_to_kind    smallint    CHECK (released_to_kind IN (1, 2)),
    released_to_id      bigint,
    release_op_key      text        UNIQUE,
    CHECK ((state = 1) = (released_at IS NULL))
);

CREATE INDEX escrow_owner_state ON escrow (owner_character_id, state);
CREATE INDEX escrow_held ON escrow (state) WHERE state = 1;

-- The items an escrow holds: a stack (instance_id 0) or one instance with its snapshot (properties as stored in
-- item_instances, the stolen and non-tradable flags carried through), so a release recreates it unchanged.
CREATE TABLE escrow_items (
    escrow_id     text     NOT NULL REFERENCES escrow (escrow_id),
    seq           smallint NOT NULL CHECK (seq >= 0),
    def_id        text     NOT NULL,
    rarity        smallint NOT NULL CHECK (rarity >= 0),
    stolen        boolean  NOT NULL,
    count         integer  NOT NULL CHECK (count > 0),
    instance_id   bigint   NOT NULL CHECK (instance_id >= 0),
    non_tradable  boolean  NOT NULL,
    properties    jsonb,
    PRIMARY KEY (escrow_id, seq),
    CHECK (instance_id = 0 OR count = 1)
);
