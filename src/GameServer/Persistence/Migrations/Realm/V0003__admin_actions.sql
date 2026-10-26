-- Realm database, schema v3 (backlog J admin panel v0, spec 7.4): the admin action log.
-- Every admin action is one row: who (admin_name), when (requested_at), what (action), target (character, account).
-- A command (kick) is also the request the game server executes: the panel writes it once per request_id (idempotency
-- key), the game server that holds the character claims it, runs it on its world tick and writes the outcome back.
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead.

CREATE TABLE admin_actions (
    action_id           bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    -- Made by the panel per request: a double submit or a retry finds the same row (rule 5).
    request_id          uuid        NOT NULL UNIQUE,
    realm_id            integer     NOT NULL,
    -- The admin identity that did it (M1: the dev admin name; production admin auth is TODO(open)).
    admin_name          text        NOT NULL,
    action              text        NOT NULL
        CHECK (action IN ('sign_in', 'search', 'view_character', 'view_logs', 'view_telemetry', 'kick')),
    -- characters lives in this database, but rows stay when a character is purged: no foreign key.
    target_character_id bigint      NULL,
    -- accounts lives in the global database (R-7.1-03): no foreign key.
    target_account_id   bigint      NULL,
    -- The game server instance that must execute a command (the holder of the character's ownership lock).
    target_server       text        NULL,
    -- Search terms, the log file viewed, the admin's note on a kick. Bounded by AdminSettings.maxDetailChars.
    detail              text        NULL,
    state               text        NOT NULL CHECK (state IN ('pending', 'claimed', 'done')),
    -- AdminOutcome name once done.
    outcome             text        NULL,
    requested_at        timestamptz NOT NULL,
    claimed_at          timestamptz NULL,
    claimed_by          text        NULL,
    completed_at        timestamptz NULL,
    CONSTRAINT admin_actions_done_has_outcome
        CHECK ((state = 'done') = (outcome IS NOT NULL AND completed_at IS NOT NULL)),
    CONSTRAINT admin_actions_claim_fields
        CHECK (state = 'done' OR ((state = 'pending') = (claimed_at IS NULL AND claimed_by IS NULL)))
);

CREATE INDEX admin_actions_character ON admin_actions (target_character_id, action_id DESC)
    WHERE target_character_id IS NOT NULL;

-- The open commands a game server polls for.
CREATE INDEX admin_actions_open ON admin_actions (realm_id, target_server, action_id)
    WHERE state <> 'done';
