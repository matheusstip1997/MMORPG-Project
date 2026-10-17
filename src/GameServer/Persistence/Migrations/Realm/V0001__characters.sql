-- Realm database, schema v1 (R-2.1-03, R-7.1-03): characters.
-- Items, skills and hotbar come with their own backlog items and reference characters.character_id.
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead.

CREATE TABLE characters (
    character_id      bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    realm_id          integer     NOT NULL,
    -- accounts lives in the global database, so there is no foreign key (R-7.1-03).
    account_id        bigint      NOT NULL,
    slot              smallint    NOT NULL CHECK (slot >= 0),
    name              text        NOT NULL,
    class_id          text        NOT NULL,

    -- Class 2 state (R-7.1-01): single-player gains.
    level             integer     NOT NULL CHECK (level >= 1),
    exp               bigint      NOT NULL CHECK (exp >= 0),

    -- Volatile state (R-7.1-01). Positions in integer cm, facing as a 16-bit binary angle (spec 2.2).
    world_id          text        NOT NULL,
    pos_x_cm          integer     NOT NULL,
    pos_y_cm          integer     NOT NULL,
    pos_z_cm          integer     NOT NULL,
    facing            integer     NOT NULL CHECK (facing BETWEEN 0 AND 65535),
    hp                integer     NOT NULL CHECK (hp >= 0),
    mp                integer     NOT NULL CHECK (mp >= 0),
    stamina           integer     NOT NULL CHECK (stamina >= 0),

    -- Optimistic concurrency (R-7.1-04): every write is "WHERE version = expected" and bumps it.
    version           bigint      NOT NULL,
    -- Id of the save that wrote the current version, so a retried save is recognised (idempotency).
    last_save_id      uuid        NULL,
    -- Idempotency key of the create request, so a double submit returns the same character.
    create_request_id uuid        NOT NULL,

    created_at        timestamptz NOT NULL,
    updated_at        timestamptz NOT NULL,

    -- Soft delete with a grace period (R-2.1-05).
    deleted_at        timestamptz NULL,
    purge_after       timestamptz NULL,
    CONSTRAINT characters_deletion_fields CHECK ((deleted_at IS NULL) = (purge_after IS NULL))
);

-- Names are unique per realm, ignoring case (R-2.1-05). Both keys cover characters pending deletion:
-- the card keeps its slot (R-9.10-04) and the name stays reserved until the purge.
-- TODO(open): whether a deleted character can be restored, and so whether its name could be freed early (spec 10, M4).
CREATE UNIQUE INDEX characters_realm_name_key ON characters (realm_id, lower(name));
CREATE UNIQUE INDEX characters_realm_account_slot_key ON characters (realm_id, account_id, slot);
CREATE UNIQUE INDEX characters_create_request_key ON characters (create_request_id);
