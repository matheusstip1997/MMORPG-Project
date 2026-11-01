-- Realm database, schema v15 (WP-M3-8 enchanting, spec 5.3 R-5.3-04, 7.1, 8.2): craft_ops takes the enchant attempt
-- (kind 5) and the gem operations of WP-M3-9 (6 socket, 7 removal) in one widening of its kind check, so whichever of the
-- two work packages merges first carries it and the other drops its own copy. An enchant row names the item as def_id and
-- output_item_id, its instance as output_instance_id, the stones it consumed in inputs ("<itemId>:<rarity>:<count>", always
-- consumed: an item sink, R-4.2-04), the level before and after in enchant_from and enchant_to (0 for every other kind),
-- and the logged seed of its draw (rule 6). The level itself lives in the instance's properties (item_instances,
-- spec 4.1), written by the same character save.
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead.

ALTER TABLE craft_ops
    DROP CONSTRAINT craft_ops_kind_check,
    ADD CONSTRAINT craft_ops_kind_check CHECK (kind BETWEEN 1 AND 7),
    ADD COLUMN enchant_from smallint NOT NULL DEFAULT 0 CHECK (enchant_from BETWEEN 0 AND 9),
    ADD COLUMN enchant_to   smallint NOT NULL DEFAULT 0 CHECK (enchant_to BETWEEN 0 AND 9);
