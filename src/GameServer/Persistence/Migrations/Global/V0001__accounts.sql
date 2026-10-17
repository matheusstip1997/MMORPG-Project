-- Global database, schema v1 (R-2.1-03, R-7.1-03): accounts keyed by SteamID (R-2.1-01).
-- Bans, the realm directory and character counts per realm come in later migrations.
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead.

CREATE TABLE accounts (
    account_id     bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    steam_id       bigint      NOT NULL,
    -- Family Sharing owner (R-2.1-02); null when the account owns the game itself.
    owner_steam_id bigint      NULL,
    created_at     timestamptz NOT NULL
);

CREATE UNIQUE INDEX accounts_steam_id_key ON accounts (steam_id);
