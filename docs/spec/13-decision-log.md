# 13. Decision Log
The log starts with the review round of **2026-10-15**. Decisions made before that date are already consolidated in the sections above.

| Date | Decision | Sections |
|---|---|---|
| 2026-10-15 | Rank and level are independent; overlapping rank bands (F 0-25 ... SS 70-130); small exp bonus below the band minimum | 3.6 |
| 2026-10-15 | One realm at launch, realm-aware architecture; global and realm databases; 4 character slots per realm; players in several regions | 2.1, 7.1 |
| 2026-10-15 | Dodge has i-frames again; dodge and parry pools separate | 3.1, 3.3 |
| 2026-10-15 | Lag compensation: 200 ms cap; PvP split policy (100 ms attacker rewind, defender rule for dodge/parry); 20 Hz tick | 3.1, 1.6 |
| 2026-10-15 | Network security: signed single-use tickets, reliable channel encrypted, integrity tag on client inputs, admin via VPN/IP allowlist, Shield Standard | 2.5 |
| 2026-10-15 | Save classes: synchronous transfers (including outpost sales), class 2 batched every 1 s, volatile state every 10 s, ordering barrier | 7.1 |
| 2026-10-15 | Class advancement at 30, 50, 80; gauge at 100, locked and greyed in the HUD until then | 3.5, 9.3 |
| 2026-10-15 | Main faucet plus the black market as a small secondary faucet; NPC vendors sell low-tier items and never buy | 4.2 |
| 2026-10-15 | Party drops are locked to the party for a window (no stolen mark); stolen mark only for death loot bags | 4.3 |
| 2026-10-15 | Robe cannot be activated while flagged or in combat; attacking removes it; party and guild see through it | 6.2 |
| 2026-10-15 | Lawless city: kills do not change honor, death drops apply, stolen mark without honor loss; regular dungeons: no drops, no honor, no PvP | 2.3, 6.3, 6.5 |
| 2026-10-15 | Raid entry: party picks a tier, every member meets its rank and minimum level; monsters show recommended levels | 6.7, 3.6 |
| 2026-10-15 | Arena normalization also covers Sense/Detect, stamina, charge pools, movement speed | 6.6 |
| 2026-10-15 | Merchant discount only on auction and shop fees and sale taxes | 4.2 |
| 2026-10-15 | `Shared` depends only on MemoryPack and is compiled as C# 9 | 1.5 |
| 2026-10-15 | No heavy attacks; skills consume stamina; Milestone 1 starting values adopted as placeholders | 3.1, 12.7 |
| 2026-10-15 | Player-posted quests paid in gold removed; only server-generated outpost quests (exp) remain; runs are exp only | 5.5, 4.2, 7.1, 8.2, 9.3, 12 |
| 2026-10-15 | Build-ups added (Blood loss, Burn, Cold, Poison, Lightning): 0-100 meters, repeat penalty, decay, dodge/parry block them | 3.8 |
| 2026-10-16 | HUD: only your own build-ups are shown (never other players', including PvP, monsters', or bosses'); normal monsters show a small HP bar below them in the world | 3.8, 9.3 |
| 2026-10-16 | Hot-message bit writers live in `Shared` as our own bit reader/writer, not LiteNetLib's (`Shared` depends only on MemoryPack) | 11.2 |
| 2026-10-16 | Simulation positions are `int` cm in Q24.8 fixed point (1/256 cm); facing is a 16-bit binary angle (0 = +Y, clockwise), quantized to the input frame's bit count; `IRng` is PCG32 with logged seed and stream | 1.5, 2.2 |
| 2026-10-16 | Login reason codes `InvalidTicket` (unreadable, bad signature, unknown key, wrong realm, reused) and `TicketExpired` added | 9.10, 2.5 |
| 2026-10-16 | Channel crypto is ChaCha20-Poly1305 wrapping whole messages in the transport, not a LiteNetLib packet layer; ticket v2 carries the sealed session key; login reason code `TooManyAttempts` added | 2.5, 9.10 |
| 2026-10-16 | Walkable surfaces can overlap (bridges, floors, caves): snapshot positions are 3D, movement stays 2D; wire bit widths are protocol constants (changes bump `ProtocolVersion`), not config | 1.7, 2.3, 10.4 |
| 2026-10-17 | Event bus delivers in publish order (= event-ID order, IDs per world run) with a per-dispatch cap; consumers are idempotent by event ID. Telemetry v1: fixed-size records (envelope, subject, target, 4 values) named per kind by a versioned schema, a lock-free bounded queue that drops and counts when full, JSON lines files for M1 | 1.9, 7.2, 8.5 |
| 2026-10-17 | Persistence v1: Npgsql + Dapper; migrations are versioned SQL files (Flyway naming) applied by our own `MigrationRunner` instead of EF Core or Flyway; dev and test use two databases (global, realm) on one PostgreSQL, not two schemas | 7.1 |
| 2026-10-17 | Server world loop (C1): tick schedule on a monotonic clock (`ITickClock`), absolute deadlines, resync instead of catch-up bursts; one input frame applied per player per tick, per-session queue drops the oldest when full (`ServerLoop.MaxQueuedInputFrames`); the client corrects only above its tolerance, the server sends authoritative own state plus input ack in every snapshot | 1.6, 2.2 |
| 2026-10-17 | M1 interest area is a world-aligned square with half-size `InterestRadiusCm` (footprint plus margin) until the camera footprint is defined; the observer's own entity is the first entity of its snapshots; snapshot axes are Y-up, so the ground-plane Y of `MovementSim` is sent as Z | 1.7, 2.3 |
| 2026-10-15 | Document: decision log, rule IDs, open items tagged by milestone, Milestone 1 quick spec, markdown as the source of truth | all |
