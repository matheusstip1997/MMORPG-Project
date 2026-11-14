-- Realm database, schema v20 (WP-M4-7, spec 4.5, 7.1, 8.2; M4 decision 6): listings and the auction house.
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead. The number is final at merge (wave 1 packages
-- merge one at a time); MigrationRunner applies every known unapplied version in ascending order.

-- One row per listing (auction and, from WP-M4-8, stalls; one model, spec 4.5). ListingService is its only writer. The units
-- are held in escrow (escrow_id, holder kind 2 listing or 3 stall): a create writes this row in the seller's character save
-- with the escrow hold and the listing fee; a buy updates it in the buyer's save with the escrow release and the proceeds
-- mail (guarded by state, count_left and escrow_id: a buy of part of a stack moves the rest to a new escrow, so escrow_id
-- changes); a cancel or an expiry closes it in the transaction that releases the escrow to mail. listing_id is assigned by
-- the world (one world per realm in M4; TODO(M7) a shared sequence when worlds shard). kind: 1 auction, 2 stall. state:
-- 1 active, 2 sold out, 3 cancelled, 4 expired. The item columns are copied from the item tables at the create, so the
-- search reads one table: category (ItemCategory), rarity (RarityDef.tier), min_level (ItemDef.minLevel), job_net_id
-- (JobDef.netId of ItemDef.jobId, 0 none) and armor_tier (ItemDef.armorTier of WP-M4-13: 1 light, 2 medium, 3 heavy, 0 none). The seller is never
-- shown to other players (rule 8; user answer 2026-11-13).
CREATE TABLE listings (
    listing_id     bigint      PRIMARY KEY CHECK (listing_id > 0),
    create_op_key  text        NOT NULL,
    kind           smallint    NOT NULL CHECK (kind IN (1, 2)),
    stall_id       text,
    seller_id      bigint      NOT NULL REFERENCES characters (character_id),
    escrow_id      text        NOT NULL,
    def_id         text        NOT NULL,
    item_net_id    integer     NOT NULL CHECK (item_net_id > 0),
    category       smallint    NOT NULL CHECK (category >= 0),
    rarity         smallint    NOT NULL CHECK (rarity >= 0),
    min_level      integer     NOT NULL CHECK (min_level >= 0),
    job_net_id     smallint    NOT NULL CHECK (job_net_id >= 0),
    armor_tier     smallint    NOT NULL DEFAULT 0 CHECK (armor_tier >= 0),
    unit_price     bigint      NOT NULL CHECK (unit_price > 0),
    count_initial  integer     NOT NULL CHECK (count_initial > 0),
    count_left     integer     NOT NULL CHECK (count_left >= 0),
    fee_paid       bigint      NOT NULL CHECK (fee_paid >= 0),
    created_at     timestamptz NOT NULL,
    expires_at     timestamptz NOT NULL,
    state          smallint    NOT NULL CHECK (state BETWEEN 1 AND 4),
    closed_at      timestamptz,
    close_op_key   text,
    create_save_id uuid        NOT NULL,
    CONSTRAINT listings_create_op_key UNIQUE (create_op_key),
    CONSTRAINT listings_close_op_key UNIQUE (close_op_key),
    CONSTRAINT listings_count_left_le_initial CHECK (count_left <= count_initial),
    CONSTRAINT listings_closed_when_not_active CHECK ((state = 1) = (closed_at IS NULL)),
    CONSTRAINT listings_active_has_units CHECK (state <> 1 OR count_left > 0),
    CONSTRAINT listings_sold_out_has_none CHECK (state <> 2 OR count_left = 0),
    CONSTRAINT listings_stall_has_id CHECK ((kind = 2) = (stall_id IS NOT NULL)),
    CONSTRAINT listings_expires_after_created CHECK (expires_at > created_at)
);

-- The search (cheapest unit price first, keyset pagination on (unit_price, listing_id)) reads only active rows; the narrow
-- filters that lead an index are the item ids (the name filter) and the category, the rest filter the rows an index finds.
CREATE INDEX listings_active_price ON listings (unit_price, listing_id) WHERE state = 1;
CREATE INDEX listings_active_item ON listings (item_net_id, unit_price, listing_id) WHERE state = 1;
CREATE INDEX listings_active_category ON listings (category, unit_price, listing_id) WHERE state = 1;
CREATE INDEX listings_seller ON listings (seller_id, kind) WHERE state = 1;
CREATE INDEX listings_active_expiry ON listings (expires_at) WHERE state = 1;

-- One row per purchase (a buyer takes any count of a listing; user answer 2026-11-13), written in the buyer's save with the
-- listing update: op_key is the buy's operation key (rule 5). price is what the buyer paid, tax the sale tax destroyed out
-- of it (the seller's proceeds mail carries price - tax); to_mail when the units did not fit the bag and went by mail.
CREATE TABLE listing_sales (
    op_key      text        PRIMARY KEY,
    listing_id  bigint      NOT NULL REFERENCES listings (listing_id),
    buyer_id    bigint      NOT NULL REFERENCES characters (character_id),
    count       integer     NOT NULL CHECK (count > 0),
    unit_price  bigint      NOT NULL CHECK (unit_price > 0),
    price       bigint      NOT NULL CHECK (price > 0),
    tax         bigint      NOT NULL CHECK (tax >= 0 AND tax <= price),
    to_mail     boolean     NOT NULL,
    save_id     uuid        NOT NULL,
    sold_at     timestamptz NOT NULL
);

CREATE INDEX listing_sales_listing ON listing_sales (listing_id, sold_at);
CREATE INDEX listing_sales_buyer ON listing_sales (buyer_id, sold_at);

-- A purchase that does not fit the buyer's bag arrives by mail: MailKind ListingPurchase (8; 7 is WP-M4-13's ArmorReturn, V0019).
ALTER TABLE mail DROP CONSTRAINT mail_kind_check, ADD CONSTRAINT mail_kind_check CHECK (kind BETWEEN 1 AND 8);

-- A buy of part of a held stack releases the escrow with target 3 (split: the units bought go to the buyer or their mail, the
-- rest is held again under a new escrow id in the same transaction).
ALTER TABLE escrow DROP CONSTRAINT escrow_released_to_kind_check,
    ADD CONSTRAINT escrow_released_to_kind_check CHECK (released_to_kind IN (1, 2, 3));
