# 11. Technical Setup and Delivery

## 11.1 Unity version and project structure
- **Unity 6.3 LTS**, supported until December 2027 (Unity 6.0 LTS support ends in October 2026). The version is pinned in the project and for the whole team.
- **One repository**: `src/` (server projects), `unity/` (client project), `data/` (config tables, generated world data is not stored here), `tests/`, `infra/` (infrastructure as code), `docs/`.
- **Shared code lives once** in `src/Shared`, with an assembly definition so Unity references it as a **local package**; the server project compiles the same source files. There are no copied DLLs.
- Unity assembly definitions: `Game.Shared`, `Game.Net`, `Game.Gameplay`, `Game.UI`; no circular references.
- Team practices: Git LFS for binary assets, text serialization with visible `.meta` files, Unity's YAML merge tool, small additive scenes, Addressables for content.

## 11.2 Networking and serialization
- **LiteNetLib** (decided) behind `ITransport`. Channels: unreliable/sequenced for inputs, snapshots, projectile spawns; reliable-ordered for everything else. Unity gets it through NuGetForUnity (`unity/Assets/packages.config`), pinned to the server's version; the restored DLL is committed (Git LFS) so batch mode and CI need no restore.
- **Hot messages** (inputs, snapshots, projectile spawns): hand-written bit-packed writers in `Shared` (our own bit reader/writer, because `Shared` cannot depend on LiteNetLib); quantized positions and angles; no allocation per packet.
- **Reliable messages** (inventory, trade, chat, quests, login, guild): **MemoryPack** (source-generator based; needs Unity 2022.3.12f1 or higher, installed through NuGetForUnity; limited schema evolution by default). Rule: only append fields at the end and keep the protocol version check. M1 (backlog G, user decision 2026-10-24): MemoryPack's generated code differs on .NET 7+, so `Shared` multi-targets **netstandard2.1** (C# 9, what Unity compiles) and **net10.0** (the server's build; C# 11 only for the generated code). In Unity, `MemoryPack.Core` with `System.Collections.Immutable` and `System.Runtime.CompilerServices.Unsafe` is committed under `unity/Assets/Packages` like LiteNetLib, and the generator DLL sits in `src/Shared/Analyzers` next to `Game.Shared.asmdef` (labelled `RoslynAnalyzer`), because Unity runs an analyzer on a package assembly only from that assembly's folder. Framing: one `ReliableMessageId` byte, then the MemoryPack body; decoders pick the type from the id and return false on anything malformed (`Shared/Protocol/LootMessages.cs`, `ReliableMessages`).
  - Hostile input (local chat, 2026-10-25): MemoryPack does not validate its string headers. Fuzzing a chat message found a UTF-8 string header that made it read past the buffer: an access violation that crashes the process and no `catch` can stop. So every reader of a message with strings first walks the body with `Shared/Protocol/MemoryPackGuard` (exact member count, string bounds, strict UTF-8, the whole body consumed; R-2.5-04) and only then calls MemoryPack (`ChatCodec`). A message's guard layout lists its members in order and grows with them. Golden-byte tests on both sides (`ChatMessageGoldenTests`, Unity `ChatMessageTests`) prove the two builds of `Shared` write the same bytes.
- **Database:** item instance properties and other flexible data are stored as **versioned JSON (PostgreSQL JSONB)**, which is readable and queryable.

## 11.3 Environments, CI, delivery
| Environment | Purpose | Setup |
|---|---|---|
| **Dev** | each developer, local | `docker-compose` with PostgreSQL, Redis, game server, auth API, admin panel; dev login instead of Steam; no cloud cost |
| **Staging** | integration tests, load tests, Steam beta branch | same infrastructure code as production, small, **stopped outside working hours** (scheduled start/stop) |
| **Production** | live game | smallest setup that works, scaled with population |

- **Infrastructure as code** (AWS CDK in C#, or Terraform) in `infra/`; the same definitions create staging and production with different sizes.
- **CI/CD (GitHub Actions):** build, unit tests, integration tests with real PostgreSQL and Redis in containers, `DataValidator`, UnityExporter in batch mode, deploy to staging on merge, **manual approval** for production. Secrets live in a parameter store, never in the repo.

**Flow: CI/CD pipeline**

```mermaid
flowchart LR
    n0["Push or merge"]
    n1["Build and unit tests"]
    n2["Integration tests with PostgreSQL and Redis"]
    n3["DataValidator and UnityExporter checks"]
    n4["Deploy to staging"]
    n5["Smoke load test (about 20 bots)"]
    n6{"Manual approval?"}
    n7["Deploy to production"]
    n8["Stay on staging"]
    n0 --> n1
    n1 --> n2
    n2 --> n3
    n3 --> n4
    n4 --> n5
    n5 --> n6
    n6 -->|"yes"| n7
    n6 -->|"no"| n8
    classDef neg fill:#f6dde0,stroke:#a6414b;
    classDef pos fill:#ddebd5,stroke:#4c7a34;
    class n8 neg;
    class n7 pos;
```

## 11.4 AWS cost plan (keep it minimal)
- **Account:** new accounts (since July 15, 2025) get **$100 in credits at signup and up to $100 more** by completing onboarding activities, on a free plan that lasts up to 6 months or until credits run out; EC2 and RDS usage draws from that balance. Choose the **paid plan** to avoid the free plan closing the account, keep the credits (valid up to 12 months from signup), and set **AWS Budgets alerts from day one**. Verify current terms in the AWS console.
- **Early alpha:** one **ARM (Graviton, t4g)** EC2 instance running the game server, auth API, admin panel, a reverse proxy, and Redis in containers. PostgreSQL either on the same instance (daily dumps to S3) or on **RDS** (the free plan covers db.t3.micro and db.t4g.micro for PostgreSQL).
- **Later:** RDS with point-in-time recovery and a separate Redis once there are several processes and real players.
- **Savings:** Graviton instances, staging stopped when idle, load tests on short-lived **spot** instances, one region chosen by player location (price and latency). Outbound data transfer and public IPv4 addresses are billed: estimate them in the AWS pricing calculator. UDP game traffic per player is small.

## 11.5 Load-test plan
- **Tool:** a headless `BotClient` (console app using `Shared` and LiteNetLib) that logs in with dev tickets and runs scripted behaviors: move, fight, loot, sell, trade, craft, queue for the arena or a raid.
- **Scenarios:** (1) crowded city (interest management and visibility), (2) field combat with monsters, (3) siege-size instance (100-200 players), (4) transaction concurrency (double trade, double claim, disconnect mid-operation), (5) login storm with the queue, (6) 24-hour soak.
- **Ramp:** 10, 50, 100, 300, 500 bots; first locally, then on staging from a separate machine, with simulated latency and packet loss.
- **Proposed pass criteria:** tick time p99 below half the tick budget at 300 players; bandwidth per client under a set target; no GC pauses that break ticks; **zero duplicated items**; no database deadlocks. A small smoke test (about 20 bots, 5 minutes) runs in CI on every merge.

## 11.6 UnityExporter specification
- **Authoring in Unity:** marker components `ServerBlocker` (walls, buildings, big rocks), `RegionVolume` (with the region flags), `ServerMarker` (spawn points, resource nodes, portals, outposts, shop spots, guard posts), plus the terrain.
- **Outputs** (generated into `data/world/` or CI artifacts): chunked **heightmap**; simplified **blocker shapes** with a grid index; a **walkability grid** (A* pathfinding and line of sight run on it); the **per-cell region array**; a markers file; templates for instances (dungeons, arenas, jail, raids, sieges); a **manifest** (version, bounds, cell size, source hashes, tool version). Start with heightmap, blockers, and the walkability grid; add a navmesh export only if monsters need it.
- **Batch mode:** the exporter runs headless in CI with deterministic, sorted output. The validator checks marker IDs against the config tables, region conflicts, portal pairs, bounds, and size limits, and fails the build with a JSON report.
- **Storage and versioning:** scenes stay in Git; generated world data is a CI artifact stored in S3. The server verifies the manifest version at startup and loads chunks lazily.
- **M1 (v0):** authoring components and the editor-only exporter live in `unity/Assets/Game/WorldAuthoring` (asmdefs `Game.WorldAuthoring`, `Game.WorldAuthoring.Editor`); `WorldExportSettings` in the scene holds the cell size and agent radius (recorded in the manifest). Outputs: `manifest.json`, `grid.bin` (walkability, region index and height per cell, one chunk), `regions.json`, `markers.json`, `blockers.json`; file formats in `Shared/World/WorldFiles`. M1 blockers and region volumes are boxes turned only in 90-degree steps around the vertical axis. The small sample world `data/world/m1` (scene `Assets/Scenes/M1Sample.unity`) is committed until worlds move to CI artifacts; an EditMode test fails when it is stale. Batch export: `Unity.exe -batchmode -quit -projectPath unity -executeMethod Game.WorldAuthoring.Editor.WorldExporterBatch.Export` (report in `unity/Logs/world-export-report.json`). The exporter validates what the scene alone can tell (region overlaps, walkable cells without a region, marker ids and positions, shapes, size limits); `DataValidator` (`--world`, default `data/world/m1`) and the server check the files against the config tables (manifest version, hashes, `RegionDef` ids, the `ServerLoop.PlayerSpawnMarkerId` player spawn, and a `monsterSpawn` marker for every `SpawnPoints` row: `WorldLoader.CheckConfigMarkers`). Portals, instance templates and navmesh are not exported yet.

**Flow: UnityExporter pipeline**

```mermaid
flowchart LR
    n0["Designers place markers and volumes in Unity"]
    n1["Validate in the editor"]
    n2["CI runs exporter in batch mode"]
    n3["Deterministic world files and manifest"]
    n4["Validator checks IDs, regions, portals, bounds"]
    n5["Upload artifact to S3"]
    n6["Game server loads it at startup after checking the manifest"]
    n0 --> n1
    n1 --> n2
    n2 --> n3
    n3 --> n4
    n4 --> n5
    n5 --> n6
    classDef neg fill:#f6dde0,stroke:#a6414b;
    classDef pos fill:#ddebd5,stroke:#4c7a34;
```
