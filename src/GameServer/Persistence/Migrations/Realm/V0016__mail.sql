-- Realm database, schema v16 (WP-M4-3 system mail, spec 4.5 M4 mail, 7.1, 7.4, 8.2; M4 decision 4). The version is a
-- proposal: the M4 orchestrator assigns the final number at merge (MigrationRunner applies gaps and late lower versions).
--
-- A mail holds items and gold outside every bag until its recipient claims them (one class 1 operation through
-- InventoryService) or it expires (its attachments destroyed, a sink). Only MailService and MailWriter write these
-- tables, and always inside the transaction of the operation that produced the attachments: the character save that
-- carries the send (a listing buy, a roll, a contract) or a stand-alone transaction (an admin compensation, a world job).
-- A mail's recipient is never written by the send: the carrier's save holds it, so a settlement writes one character row
-- (R-7.1-02). mail_ops is the audit trail of every send, claim and expiry with the operation key as primary key (rule 5),
-- so a mailbox can be rebuilt (R-7.1-06).
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead.

CREATE TABLE mail (
    mail_id      bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    recipient_id bigint      NOT NULL REFERENCES characters (character_id),
    -- MailKind (Shared.Protocol): 1 listing return, 2 listing proceeds, 3 loot overflow, 4 contract return,
    -- 5 admin compensation, 6 loot bag return. Append only.
    kind         smallint    NOT NULL CHECK (kind BETWEEN 1 AND 6),
    -- A UiStrings id (mail.subject.*) and its typed arguments [{"n": name, "k": kind, "v": value}]: never free text.
    subject_id   text        NOT NULL CHECK (length(subject_id) BETWEEN 1 AND 48),
    subject_args jsonb       NOT NULL DEFAULT '[]'::jsonb,
    -- The gold still attached (a partial claim takes it when it fits).
    gold         bigint      NOT NULL CHECK (gold >= 0),
    -- 0 open, 1 claimed (emptied), 2 expired (its attachments destroyed).
    state        smallint    NOT NULL DEFAULT 0 CHECK (state BETWEEN 0 AND 2),
    created_at   timestamptz NOT NULL,
    expires_at   timestamptz NOT NULL,
    closed_at    timestamptz NULL,
    -- The operation that wrote it (the caller's key): one mail per operation key.
    send_op_key  text        NOT NULL,
    -- The character whose save carried the send (the buyer, the picker, the crafter); null for a stand-alone send.
    carrier_id   bigint      NULL,
    source       text        NOT NULL,
    CONSTRAINT mail_send_op_key UNIQUE (send_op_key),
    CONSTRAINT mail_closed_when_not_open CHECK ((state = 0) = (closed_at IS NULL)),
    CONSTRAINT mail_expires_after_created CHECK (expires_at > created_at)
);

-- A login's open mails and the badge count.
CREATE INDEX mail_recipient_open ON mail (recipient_id, created_at) WHERE state = 0;
-- The expiry job.
CREATE INDEX mail_expiry_open ON mail (expires_at) WHERE state = 0;

CREATE TABLE mail_items (
    mail_id      bigint   NOT NULL REFERENCES mail (mail_id),
    idx          smallint NOT NULL CHECK (idx BETWEEN 0 AND 7),
    def_id       text     NOT NULL,
    rarity       smallint NOT NULL CHECK (rarity >= 0),
    -- The units still attached; the row goes when a claim takes the last unit.
    count        integer  NOT NULL CHECK (count > 0),
    -- An instance snapshot keeps its id (its creator stays in the id, spec 4.1), flags and properties; 0 for a stack, or for
    -- an instance minted at the claim (a ground drop that never was an instance).
    instance_id  bigint   NOT NULL DEFAULT 0 CHECK (instance_id >= 0),
    stolen       boolean  NOT NULL DEFAULT false,
    non_tradable boolean  NOT NULL DEFAULT false,
    properties   jsonb    NULL,
    PRIMARY KEY (mail_id, idx),
    CONSTRAINT mail_items_instance_is_one CHECK (instance_id = 0 OR count = 1)
);

CREATE TABLE mail_ops (
    -- Send: the caller's key; claim: the claim's key and the mail id; expiry: mailExpire:<mail id>.
    op_key       text        PRIMARY KEY,
    -- 1 send, 2 claim, 3 expire.
    kind         smallint    NOT NULL CHECK (kind BETWEEN 1 AND 3),
    mail_id      bigint      NOT NULL REFERENCES mail (mail_id),
    -- The recipient (send, expiry) or the claimer.
    character_id bigint      NOT NULL,
    -- Gold moved: attached, claimed or destroyed.
    gold         bigint      NOT NULL CHECK (gold >= 0),
    -- Items moved: [{"i": idx, "d": defId, "r": rarity, "c": count, "n": instanceId}].
    items        jsonb       NOT NULL DEFAULT '[]'::jsonb,
    save_id      uuid        NULL,
    created_at   timestamptz NOT NULL
);

CREATE INDEX mail_ops_mail ON mail_ops (mail_id, created_at);
CREATE INDEX mail_ops_expired ON mail_ops (created_at) WHERE kind = 3;

-- The admin panel's compensation (spec 7.4): a command any game server of the realm executes (target_server null), with
-- its items and gold as the payload; the server writes the mail and the outcome in one transaction.
ALTER TABLE admin_actions
    DROP CONSTRAINT admin_actions_action_check,
    ADD CONSTRAINT admin_actions_action_check
        CHECK (action IN ('sign_in', 'search', 'view_character', 'view_logs', 'view_telemetry', 'kick', 'send_mail')),
    ADD COLUMN payload jsonb NULL;
