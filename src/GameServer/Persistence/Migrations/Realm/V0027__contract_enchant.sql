-- Realm database, schema v27 (WP-M4-10 part C, spec 5.4, 5.3 R-5.3-04; user answers 2026-11-16): enchant contracts. Never edit an
-- applied migration (R-7.1-04): add a new V<n>__<name>.sql instead. The number is final at merge.

-- contracts.kind is ForgeKind: 3 is an enchant contract (def_net_id the item's ItemDef net id, def_id the item, job_id the item's
-- job). Its terms name two slots: the customer's equipment instance (with "instanceId" and its "enchantLevel" at the proposal) and
-- the stones, either side's. One attempt at the item job's station completes it, whatever the outcome; the item goes back to the
-- customer by mail (ContractOutput) as the same instance at its new level, so no other column changes.
ALTER TABLE contracts DROP CONSTRAINT contracts_kind_check, ADD CONSTRAINT contracts_kind_check CHECK (kind IN (1, 2, 3));
