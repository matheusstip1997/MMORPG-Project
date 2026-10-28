-- Realm database (WP-M2-6, spec 5.5, 7.1): outpost runs, their steps and the audit of every run operation.
-- All of it is save class 2 (R-7.1-01): written by the character save that carries it, in the same transaction as the
-- version-checked characters update, the exp it paid and the inventory batch that removed delivered items (rule 5). Saves write
-- runs and steps as absolute rows (upserts), so a retried save writes the same thing.
-- V0010 follows V0008 (WP-M2-5, skill tree) and V0009 (WP-M2-8, Bystial book), which merge first (orchestrator, 2026-10-27).
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead.

-- One row per run (spec 5.5 Run). owner_party_id stays NULL until party runs (M4). state is the RunState enum: 1 Active,
-- 2 Completed, 3 Claimed, 4 Abandoned. claimed_exp is what the one claim paid (R-5.5-03), with the bonus and penalty applied.
CREATE TABLE quest_runs (
    run_id             uuid        PRIMARY KEY,
    owner_character_id bigint      NOT NULL REFERENCES characters (character_id),
    owner_party_id     bigint      NULL,
    outpost_marker_id  text        NOT NULL,
    state              smallint    NOT NULL CHECK (state BETWEEN 1 AND 4),
    board_epoch        bigint      NOT NULL CHECK (board_epoch >= 0),
    started_at         timestamptz NOT NULL,
    bonus_percent      integer     NOT NULL CHECK (bonus_percent >= 0),
    penalty_percent    integer     NOT NULL CHECK (penalty_percent BETWEEN 0 AND 100),
    claimed_exp        bigint      NOT NULL DEFAULT 0 CHECK (claimed_exp >= 0),
    updated_at         timestamptz NOT NULL,
    save_id            uuid        NOT NULL
);

-- QuestCaps.maxActiveRuns is 1 in M2 (its range allows nothing else): a character has at most one run that is not claimed or
-- abandoned, even if two worlds wrote at once. Raising the cap needs a migration that drops this index.
CREATE UNIQUE INDEX quest_runs_one_open_per_character ON quest_runs (owner_character_id) WHERE state IN (1, 2);

-- One row per step (spec 5.5 RunStep). kind is QuestKind (1 kill, 2 deliver, 3 returnItem); state is RunStepState (0 active,
-- 1 done, 2 forfeited). reward_exp was locked at acceptance (R-5.5-02) with the demand reward percent it was scaled by.
CREATE TABLE quest_run_steps (
    run_id         uuid     NOT NULL REFERENCES quest_runs (run_id),
    step_index     smallint NOT NULL CHECK (step_index >= 0),
    kind           smallint NOT NULL CHECK (kind BETWEEN 1 AND 3),
    quest_def_id   text     NOT NULL,
    monster_def_id text     NOT NULL,
    item_def_id    text     NULL,
    required       integer  NOT NULL CHECK (required > 0),
    progress       integer  NOT NULL CHECK (progress >= 0 AND progress <= required),
    reward_exp     bigint   NOT NULL CHECK (reward_exp >= 0),
    reward_percent integer  NOT NULL CHECK (reward_percent >= 0),
    state          smallint NOT NULL CHECK (state BETWEEN 0 AND 2),
    PRIMARY KEY (run_id, step_index)
);

-- One row per accept, delivery, forfeit and claim (rule 5): the audit row, and with op_key the idempotency key. action is
-- QuestRunAction (1 accept, 2 deliver, 3 forfeit, 4 claim); amount is the units delivered or the exp claimed. A run is claimed
-- once ever: a second claim row of the same run fails the whole save.
CREATE TABLE quest_run_audit (
    op_key       text        PRIMARY KEY,
    run_id       uuid        NOT NULL REFERENCES quest_runs (run_id),
    character_id bigint      NOT NULL REFERENCES characters (character_id),
    action       smallint    NOT NULL CHECK (action BETWEEN 1 AND 4),
    step_index   smallint    NULL,
    amount       bigint      NOT NULL CHECK (amount >= 0),
    save_id      uuid        NOT NULL,
    created_at   timestamptz NOT NULL
);

CREATE UNIQUE INDEX quest_run_audit_one_claim ON quest_run_audit (run_id) WHERE action = 4;
CREATE INDEX quest_run_audit_character ON quest_run_audit (character_id, created_at);
