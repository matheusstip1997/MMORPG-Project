-- Realm database, schema v24 (WP-M4-15; spec 4.5 R-4.5-02, 7.1, 7.4, 7.6; M4 decisions 13 and 17). The version is a
-- number assigned by the M4 orchestrator for the merge order (V0023 is the social package, WP-M4-16; MigrationRunner
-- applies every known unapplied version in ascending order, so a gap is fine).
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead.
--
-- Large one-way transfers between accounts are flagged (R-4.5-02): a scan off the tick (Persistence/TransferFlagWorker) reads
-- the committed transfers between two characters (trades from transfer_ops, auction and stall purchases from listing_sales),
-- sums what each account of a pair gave the other in a UTC day, and records a flag when the net gold or the net value
-- (gold plus the items' ItemValueBand value) reaches a TransferFlagSettings threshold. A flag moves no value and changes no
-- transfer: nothing is refused and the players see nothing; the admin panel lists the flags.

-- Every transfer the scan counted, once ever (rule 5: the key makes a rescan, a retry or a second server count nothing twice).
-- source: 1 trade (transfer_ops.op_key), 2 listing sale (listing_sales.op_key).
CREATE TABLE transfer_flag_ops (
    source      smallint    NOT NULL CHECK (source IN (1, 2)),
    op_key      text        NOT NULL,
    day         date        NOT NULL,
    scanned_at  timestamptz NOT NULL,
    PRIMARY KEY (source, op_key)
);

-- What two accounts gave each other in one UTC day (the lower account id first): the gold and the items' value each way.
CREATE TABLE transfer_pair_days (
    day                    date        NOT NULL,
    account_low            bigint      NOT NULL,
    account_high           bigint      NOT NULL,
    gold_low_to_high       bigint      NOT NULL CHECK (gold_low_to_high >= 0),
    gold_high_to_low       bigint      NOT NULL CHECK (gold_high_to_low >= 0),
    item_value_low_to_high      bigint      NOT NULL CHECK (item_value_low_to_high >= 0),
    item_value_high_to_low      bigint      NOT NULL CHECK (item_value_high_to_low >= 0),
    transfers              integer     NOT NULL CHECK (transfers > 0),
    updated_at             timestamptz NOT NULL,
    PRIMARY KEY (day, account_low, account_high),
    CHECK (account_low < account_high)
);

-- One flag per pair and day, kept current while the pair keeps trading that day. crossed: 1 net gold, 2 net value, 3 both.
-- The giver is the account the net value left; the characters are those of the newest transfer counted, giver first.
CREATE TABLE transfer_flags (
    flag_id             bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    day                 date        NOT NULL,
    account_low         bigint      NOT NULL,
    account_high        bigint      NOT NULL,
    giver_account       bigint      NOT NULL,
    receiver_account    bigint      NOT NULL,
    giver_character     bigint      NOT NULL,
    receiver_character  bigint      NOT NULL,
    crossed             smallint    NOT NULL CHECK (crossed BETWEEN 1 AND 3),
    net_gold            bigint      NOT NULL,
    net_value           bigint      NOT NULL,
    transfers           integer     NOT NULL CHECK (transfers > 0),
    first_op_key        text        NOT NULL,
    flagged_at          timestamptz NOT NULL,
    updated_at          timestamptz NOT NULL,
    CONSTRAINT transfer_flags_pair_day UNIQUE (day, account_low, account_high),
    CHECK (account_low < account_high)
);

-- The admin list: the newest days first.
CREATE INDEX transfer_flags_day ON transfer_flags (day DESC, flag_id DESC);

-- The scan reads the transfers committed within its look-back window.
CREATE INDEX transfer_ops_created ON transfer_ops (created_at);
CREATE INDEX listing_sales_sold ON listing_sales (sold_at);

-- The economy dashboard's hourly sinks count the gold of expired mail (WP-M4-15): V0016's index mail_ops_expired serves that read.

-- The admin panel's transfer flag list is a read like the others (spec 7.4: every admin action is logged).
ALTER TABLE admin_actions DROP CONSTRAINT admin_actions_action_check;
ALTER TABLE admin_actions ADD CONSTRAINT admin_actions_action_check
    CHECK (action IN ('sign_in', 'search', 'view_character', 'view_logs', 'view_telemetry', 'kick', 'view_economy', 'send_mail',
        'view_transfer_flags'));
