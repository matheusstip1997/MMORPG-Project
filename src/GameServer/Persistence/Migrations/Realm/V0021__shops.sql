-- Realm database, schema v21 (WP-M4-8, spec 4.5, 7.1, 8.2; M4 decision 7): player shop stalls.
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead. The number is final at merge (wave 1 packages
-- merge one at a time); MigrationRunner applies every known unapplied version in ascending order.

-- One row per stall (a shop of one character on a stall place of a city; one model with the auction's listings, which name it
-- by listings.stall_id). StallService is its only writer. An open writes the row in the owner's character save with the
-- stall's first listing (its escrow hold and listing fee: one transaction); a close (at its time, sold out, by its owner, or
-- found stale at world start) updates it on its own, off the tick, guarded by state = 1, so it is written whether or not the
-- owner is online. shop_id is the open's operation key (unique for ever: it carries the world's run id). place_marker_id is
-- the stall marker it stands on; owner_name the owner's character name at the open (shown only through DisplayIdentity, rule
-- 8). state: 1 open, 2 closed. close_reason: 1 sold out (its last listing sold or was cancelled, user answer 2026-11-13),
-- 2 closed by its owner, 3 its time ran out, 4 closed at world start (past its time or with no listing left).
CREATE TABLE shops (
    shop_id            text        PRIMARY KEY,
    owner_character_id bigint      NOT NULL REFERENCES characters (character_id),
    place_marker_id    text        NOT NULL,
    owner_name         text        NOT NULL,
    opened_at          timestamptz NOT NULL,
    closes_at          timestamptz NOT NULL,
    state              smallint    NOT NULL CHECK (state IN (1, 2)),
    closed_at          timestamptz,
    close_reason       smallint    CHECK (close_reason BETWEEN 1 AND 4),
    open_save_id       uuid        NOT NULL,
    CONSTRAINT shops_closed_when_not_open CHECK ((state = 1) = (closed_at IS NULL)),
    CONSTRAINT shops_close_reason_when_closed CHECK ((state = 1) = (close_reason IS NULL)),
    CONSTRAINT shops_closes_after_opened CHECK (closes_at > opened_at)
);

-- One open stall per place and one per character (M4 decision 7): the world decides both in memory; these are the backstop.
CREATE UNIQUE INDEX shops_open_place ON shops (place_marker_id) WHERE state = 1;
CREATE UNIQUE INDEX shops_open_owner ON shops (owner_character_id) WHERE state = 1;
