-- Realm database, schema v19 (WP-M4-13, spec 4.1 R-4.1-05, 3.5 R-3.5-08, M4 decision 9b): armor per tier.
-- When a skill tree operation changes the character's armor tier, the worn pieces of the old tier come off into the bag;
-- what does not fit goes to the character's own mail in the same transaction, as the new MailKind 7 (ArmorReturn).
-- Nothing else is stored: the tier is ItemDef data and the character's tier comes from the class and the learned skills.
-- Never edit an applied migration (R-7.1-04): add a new V<n>__<name>.sql instead.

-- MailKind (Shared.Protocol): 1 listing return, 2 listing proceeds, 3 loot overflow, 4 contract return,
-- 5 admin compensation, 6 loot bag return, 7 armor return. Append only.
ALTER TABLE mail DROP CONSTRAINT mail_kind_check;
ALTER TABLE mail ADD CONSTRAINT mail_kind_check CHECK (kind BETWEEN 1 AND 7);
