# 1. Overview and Architecture

## 1.1 What this document is
This is the consolidated specification of every system and rule decided so far, with how each is built. Each system section has the same shape: **Rules** (decisions already made), **Implementation** (classes, data, algorithms), **Talks to** (events emitted and consumed, services called), and a **flow diagram**. Items still undecided are marked **Open** and collected in section 10. Numbers (rates, caps, windows) are **config values**, never constants in code.

## 1.2 Game summary
- Top-down camera at a fixed 45-degree pitch (Path of Exile / Diablo view) that the player can turn horizontally around the character (R-2.2-05), souls-like pacing: stamina, dodge, parry, slower combat that speeds up a little with level.
- One continuous main map (cities, biomes, monsters) plus instances (dungeons, raids, jail, GvG sieges). One special **lawless city**. Fast travel only between the 3 main cities.
- Three vertical progressions: **level**, **gear**, **adventurer rank** (F to SS). No premade quests: players build **runs** from server-generated quests at **Adventurer Guild outposts** (exp rewards only).
- Closed economy: gold enters the game in exactly one way (selling sell-only monster drops at outposts) and leaves through many sinks.
- Jobs (crafting, gathering, law enforcement) are separate from classes. Honor and PK systems create a crime-and-law loop. Player guilds fight for territories.
- A **ranked PvP arena** (1v1, 2v2, 3v3, best of 3 rounds, normalized stats, seasons with rank and skin rewards).
- Scale: 10-15 players at first, hundreds later. Team of 5, no deadline, phased delivery, Steam only.

## 1.3 Technology
| Area | Choice |
|---|---|
| Client | **Unity 6.3 LTS** (supported until December 2027), C# |
| Server | .NET 8+ C# game server; C++ only as a native library for a profiled hot path |
| Transport | UDP through **LiteNetLib** (reliable and unreliable channels) behind an `ITransport` interface |
| Serialization | Hybrid: hand-written bit-packed writers for hot unreliable messages (inputs, snapshots, projectile spawns); **MemoryPack** for reliable messages; versioned JSON (PostgreSQL JSONB) for item properties in the database |
| Database | PostgreSQL (AWS RDS), Redis (ElastiCache) for sessions, locks, cache |
| Login | Steam session ticket verified by the Auth API; account key = SteamID |
| Infra | AWS, handled by the networking owner; **one realm at launch, realm-aware** |
| Admin | ASP.NET Core web panel |

## 1.4 Architectural principles
1. **Server-authoritative.** The client sends inputs and requests, never results.
2. **Data-driven.** Every number and rule table is config, loaded by server and client from a shared schema, hot-reloadable on the server.
3. **Event-driven.** Gameplay systems emit events on an in-process event bus; quests, achievements, honor, job XP, demand, telemetry consume them. No system calls into another system's internals.
4. **One writer per concern.** Only `InventoryService` changes items and gold; only `FeeService` computes fees; only `HonorService` changes honor (including crafting with stolen items); only `SentencingService` jails.
5. **Atomic, idempotent operations.** Every value-moving operation is one database transaction with an idempotency key and an audit row.
6. **Deterministic simulation.** Integer or fixed-point math, fixed processing order (entity ID), server-side seeded RNG with logged seeds for disputes.
7. **Timestamps, not timers.** Deadlines, jail, cooldowns, windows are stored as UTC timestamps and evaluated on demand, so restarts and logouts never lose them.
8. **Shared code for prediction.** Movement, damage, stat and action-timing code lives in `Shared` and runs on both client and server.
9. **Telemetry from day one.** Every important event is logged asynchronously.
10. **Interfaces at the seams.** `ITransport`, `IVisibilityQuery`, `IForgeChallenge`, `IClock`, `IRng`, repositories.

## 1.5 Solution layout
```
Mmorpg.sln
src/
  Shared/                    netstandard2.1, referenced by Unity (as DLL/source) and the server
    Protocol/                Messages, PacketIds, Channels, Serializers, ProtocolVersion
    Data/                    Definitions + table loader (ItemDef, SkillDef, MoldDef, RegionDef, ...)
    Simulation/              MovementSim, DamageCalc, StatCalc, ActionTimings, FixedPoint
    Util/                    Ids, IClock, IRng, Quantize
  GameServer/
    Host/                    Program, Bootstrap, ConfigReload, TickLoop
    Net/                     ITransport, SessionManager, InputQueue, SnapshotWriter, LingerManager, LoginQueue
    World/                   World, WorldManager, SpatialGrid, InterestManager, Regions, CollisionData, Instances
    Entities/                Entity, Player, Monster, NpcGuard, Projectile, LootBag, ResourceNode, Portal, ShopStall
    Combat/                  ActionMachine, HitResolver, Hitboxes, ProjectileSystem, PoiseSystem, ParrySystem
    Stats/                   StatBlock, ModifierSource, DerivedStats, ResourceMeters
    Skills/                  SkillTree, SkillEvolution, Hotbar, ClassGauge, Respec
    Progression/             ExpService, RankService, AchievementService, JobService
    Items/                   InventoryService, Containers, ItemFactory, StolenMark, Mail
    Economy/                 DemandService, SpawnController, PriceService, FeeService, SinkLedger
    Crafting/                ForgeEngine, RecipeBook, QualityCalc, EnchantService, GemService, Conversion, Gathering
    Contracts/               ContractService, Portfolio
    Quests/                  OutpostQuests, RunService
    Social/                  Party, Chat, Friends, PartyFinder, Matchmaking
    Arena/                   ArenaQueue, MatchStateMachine, ArenaRuleset, RatingService, SeasonService
    Law/                     PvpRules, FlagService, HonorService, ArrestService, JailService, SentencingService,
                             GuardNpcAi, DisguiseService
    Guilds/                  GuildService, Territory, SiegeService, Treasury, Reputation, GuardHire
    Events/                  EventBus, EventTypes
    Persistence/             Repositories, UnitOfWork, Migrations, RedisCache, SaveScheduler
    Telemetry/               EventLog, Snapshots
  AuthApi/                   Steam ticket verification, session tickets, character list/create/delete
  AdminPanel/                roles, search, ledger view, bans, restore, config, dashboards
  Workers/                   Analytics, BotScoring, EconomySnapshots, ScheduleRunner (sieges, queues)
  Tools/                     UnityExporter, DataValidator, EconomySimulator, BotClient, LoadTester
  Tests/                     Unit, Integration, Simulation (Monte Carlo)
data/                        config tables (json/csv) + schemas
unity/Assets/Game/           Net, Input, Prediction, Entities, UI, VFX, Audio, Streaming
```

### Dependency rules
- `Shared` depends **only on MemoryPack** and is compiled as **C# 9** (the server compiles the same sources with a newer compiler, so the language version is pinned). It targets netstandard2.1 for Unity and also net10.0 for the server, because MemoryPack's generated code differs there (spec 11.2). Server modules depend on `Shared` and on interfaces of other modules, never on their internals.
- Cross-module communication is by (a) events on the bus (fire-and-forget facts), (b) service interfaces for commands that need an answer (`InventoryService.Transfer`, `FeeService.Quote`), (c) shared read-only data tables.
- Only `Persistence` talks to PostgreSQL and Redis. Gameplay code uses repositories and the unit of work.

## 1.6 Threading and the tick loop
- Each **World** (main map, each instance) owns a simulation thread running a fixed tick (**20 Hz**). Network IO threads only enqueue decoded inputs into per-session queues. Database work runs on persistence workers; results return to the owning world's thread through a completion queue.
- In-memory state of online characters is authoritative. Saving follows **three classes** (section 7.1): transfers between players or with shared state commit synchronously before confirming; single-player gains are saved in batches every second; volatile state every 10 seconds. The tick never waits on the database.
- **Tick phases, in fixed order:** (1) apply buffered inputs, one frame per player, or up to `ServerLoop.maxInputFramesPerTick` while a backlog older than `inputBacklogTicks` drains (input catch-up, spec 2.2), (2) movement and facing, (3) action state machines, (4) projectile movement, (5) hit resolution (dodge, parry, damage, poise), (6) status/gauge updates, (7) AI and spawn controller, (8) death/respawn handling, (9) visibility and interest update, (10) snapshot and event sends, (11) dispatch of queued bus events, (12) telemetry flush. Order inside a phase is by entity ID.

```
net threads -> InputQueue(session) -> [World thread: phases 1..12] -> SnapshotWriter -> ITransport
                                         |-> EventBus -> consumers (same thread, queued)
                                         |-> PersistenceWorker (async) -> PostgreSQL / Redis
```

## 1.7 Network protocol
- **Channels:** unreliable-sequenced (input frames, snapshots, projectile spawns), reliable-ordered (action confirmations, inventory, trade, chat, quests, guild). Game messages on the reliable channel start with a `ReliableMessageId` byte; the first is `ActionResult`, sent for refused actions with a reason code (section 3.1) and, from protocol 6, for parry results (reasons `ParrySuccess`, `ParryReduced`, `ParryFailedRed`, `ParryFailedArmor`, `ParryFailedTiming`, `ParryNoCharge`; spec 3.3, 9.7; from protocol 15 `ParryFailedEarly` and `ParryFailedLate` replace `ParryFailedTiming`). From protocol 10 the second is `EntityIdentity` (entity id and a bounded UTF-8 name): a player's display name, already passed through `DisplayIdentity`, sent when the player enters an observer's interest and to a player for itself (name plates, spec 9.12); snapshots never carry names. Both are hand-written layouts. From protocol 11 the loot messages are MemoryPack bodies after the id byte: `PickupRequest` (3), `PickupResult` (4), `InventoryContents` (5) (backlog G, spec 4.1, 4.3). From protocol 12 local chat adds three MemoryPack messages (spec 11.2): `ChatSend` (6, client to server: the line), `ChatMessage` (7: sender entity id, display name through `DisplayIdentity`, text) and `ChatRefused` (8: a `ChatReason`). Client requests on the reliable channel are routed by their first byte from a fixed list (`PickupRequest`, `ChatSend`, `InventoryMoveRequest`; anything else is counted as malformed, R-2.5-04) into bounded per-session queues the world drains in phase 1: chat lines at most `ChatSettings.maxQueuedLines` (R-2.5-03 reliable backlog; the rest are dropped and counted). From protocol 13 the inventory window adds `InventoryMoveRequest` (9, client to server: two slots of the Normal bag and a request id) and `InventoryMoveResult` (10: a reason code or what the move did), spec 9.13; moves share the pickups' queue, `LootSettings.maxQueuedPickupRequests` plus `InventorySettings.maxQueuedMoveRequests` long. The request has its `ReliableMessageCaps` row (8 bytes) and both have a guard layout in `ReliableMessageReader` (R-2.5-07).
- **Input frame:** sequence, client tick, WASD bits, quantized facing angle (8-10 bits), action bits (attack, dodge, parry, skill slot, interact). The last 2-3 inputs are repeated in each packet to survive loss. Protocol 4: the packet header carries the newest frame's **view tick**, the server tick the client renders other entities at (its interpolation clock, 0 before the first snapshot); older frames take it minus their client-tick distance. The server rewinds the player's hits on monsters to it, capped (R-3.1-03). Protocol 6: each frame's 6-bit sub-tick is the fraction of its view tick in 1/64 (the view clock is `ViewTick + SubTick / 64`), on which the server judges the player's dodges and parries against monsters' hits, under clock-sync limits (spec 3.1, D4 defence bullet).
- **Hit message** (protocol 4, unreliable, `HitMessage`, hot message id 4): in tick phase 10 every connected player gets the hits of the tick whose target it has in interest (or that left interest this tick, so a killing blow still arrives): attacker id, target id, HP lost, crit flag, kind (damaged, killed, dodged, parried; protocol 7: healed, with the HP gained, spec 3.1 D6). Clients use it for damage numbers only (spec 9, backlog I); HP and states come from snapshots.
- **Snapshots:** per-observer delta of entities inside the interest area (position, facing, state, HP bucket, flags: armed, red-name, disguised, mounted, pet). Spawn/despawn events when entities enter or leave interest. The observer's own entity is always the first entity of its snapshot (how the client knows which entity it controls, until a reliable enter-world message exists). Positions are **3D** (X, Y, Z in integer cm relative to an origin carried by the snapshot) because walkable surfaces overlap (bridges, floors, caves); movement and input stay 2D. Bit layouts: `Shared/Protocol` (`InputPacket`, `Snapshot`). The entity state is the action state code of section 3.1 (D2); entities in an action also carry the action id and its age in ticks and (protocol 6) the attack's parry color in 3 bits (R-3.1-05: none, red, yellow, blue, green), flinched or staggered ones the age and the length. Resources (spec 9.4, protocol 3): every entity carries a 6-bit HP bucket (`Snapshot.HpBucketBits`, a protocol constant because both sides must agree: 0 only when dead, 63 only when full, `Quantize.Bucket`); the header carries the observer's exact HP, MP and stamina with their maxima (31 bits each, behind a presence bit) and, from protocol 7, its active statuses for the buff row (count 4 bits, then per status `StatusEffectDef.netId` 12 bits, stacks 4 bits, ticks left 16 bits; at most 15, in the order they were applied; spec 3.7) and, from protocol 9, its build-up meters above 0 (count 3 bits, then per meter `BuildUpDef.netId` 4 bits and whole points 7 bits, rounded up, 100 while the effect runs; spec 3.8), every snapshot like the statuses. Other entities' statuses and meters are not sent. A spawn event's definition id is the entity's `ClassDef.netId` (players) or `MonsterDef.netId` (monsters), so the client picks its visual. **Size:** every hot message is one datagram (LiteNetLib does not fragment unreliable messages), at most `NetSecurity.maxUnreliableMessageBytes` (1000; with the 12-byte input tag it must fit the 1020 bytes LiteNetLib sends unfragmented at its initial MTU, or the server refuses to start) and the peer's current limit (`ITransport.MaxUnreliableBytes`: its MTU minus the sequenced header; R-2.5-03 packet size). A snapshot keeps room for the observer's own entity, so it stays the first entity, and leaves out the despawns, spawns and entities that do not fit, counting it (`snapshotsTruncated`; TODO: priority, resending left-out spawns and deltas when interest outgrows one datagram); hit, projectile and ground-area events go out in further messages; the transport drops any unreliable message still over the limit and counts it with the last peer and size (`SendCounters`) instead of failing the tick.
- **Projectiles** (spec 3.2, protocol 5): hot messages `ProjectileSpawn` (id 3: per spawn projectile id, `ProjectileDef.netId`, origin X/Y/height in whole cm, facing in 10 bits, speed, start tick) and `ProjectileDespawn` (id 5: projectile id, tick, reason `Hit`, `Range`, `World`, `OwnerGone`), each event repeated `ProjectileSettings.eventRepeatTicks` ticks against loss. Bit layouts: `Shared/Protocol/ProjectileMessages`. Hot message 4 is the hit message above.
- **Ground areas** (spec 3.1, protocol 9): hot messages `GroundAreaSpawn` (id 6: per spawn area id, the effect as `ActionDef.netId` 12 bits plus its index in the action 3 bits, origin X/Y/height in whole cm, facing in 10 bits, start tick) and `GroundAreaDespawn` (id 7: area id, tick, reason `Expired`, `OwnerDied`, `OwnerGone`), each event repeated `GroundAreaSettings.eventRepeatTicks` ticks against loss. A player gets a spawn while the square the area's shapes reach overlaps its interest square. Bit layouts: `Shared/Protocol/GroundAreaMessages`.
- **Clients never receive** hidden entities, other players' honor when disguised, or data outside interest.
- Every message family carries a protocol version; the login handshake refuses mismatches.

## 1.8 Configuration system
Tables are versioned files under `data/` validated by `DataValidator` (schema, ranges, referential integrity, balance sheets). The server loads them at start and can hot-reload (in dev, spec 11.7); the client loads the same tables for prediction, tooltips, and timings. Table catalog is in section 8.5.

## 1.9 Event bus
In-process publish/subscribe. Events are small immutable records with an envelope (event ID, UTC time, world, actor character ID, region). Producers publish during tick phases; consumers run in phase 11 so they never interrupt combat resolution. Consumers must be idempotent. The catalog is in section 8.1.
