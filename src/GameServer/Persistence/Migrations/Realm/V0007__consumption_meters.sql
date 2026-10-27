-- Realm database (WP-M2-2, R-3.4-03): the intoxication and satiety meters of a character, in thousandths of a point,
-- and the UTC time they were captured. They decay from that time on the next login (offline time counts), so the
-- values are what the meters showed at the save. NULL time: never captured (a character saved before this migration).
-- V0004-V0006 belong to other M2 work packages (items, exp and level, gold); the runner applies versions in any gap order.
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead.

ALTER TABLE characters
    ADD COLUMN toxicity_milli integer     NOT NULL DEFAULT 0 CHECK (toxicity_milli >= 0),
    ADD COLUMN satiety_milli  integer     NOT NULL DEFAULT 0 CHECK (satiety_milli >= 0),
    ADD COLUMN consumption_at timestamptz NULL;
