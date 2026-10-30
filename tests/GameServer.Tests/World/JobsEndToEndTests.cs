using System.Diagnostics;
using GameServer.Events;
using GameServer.Host;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Jobs;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.World;

/// <summary>
/// WP-M3-1's done criterion over the whole server loop (spec 5.1 M3 decisions): the committed world (the city's job master,
/// <c>m1.npc.jobMaster</c>) and tables, a real LiteNetLib client, sessions on Redis and PostgreSQL. A level-12 warrior at the
/// job master opens it, picks Blacksmith as its main job and is answered only after the save committed (rule 5); the pick
/// survives a relogin; a second main pick is refused with <c>JobAlreadyChosen</c>; and a test <c>Harvested</c> event (published on
/// the world thread when the pick is announced) raises the Blacksmith's job XP in the client's <see cref="JobState"/>.
/// </summary>
public sealed class JobsEndToEndTests : IAsyncLifetime
{
    private const byte Blacksmith = 1, Jeweler = 2;
    private const ushort JobMasterNetId = 3; // NpcDef m1.jobMaster
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private readonly ServerConfig _config = ServerTestEnvironment.LoadConfig();
    private ServerTestEnvironment _env = null!;
    private Bootstrap _server = null!;
    private LoopbackGameClient? _client;

    public async Task InitializeAsync()
    {
        _env = await ServerTestEnvironment.CreateAsync();
        _server = _env.StartServer(_config, beforeStart: server => server.Events.Subscribe<JobChosen>(new HarvestOnPick(server.Events)));
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _env.DisposeAsync();
    }

    /// <summary>A stand-in for WP-M3-5's harvest: one tier-1 <see cref="Harvested"/> of the picked job, on the world thread.</summary>
    private sealed class HarvestOnPick(EventBus bus) : IEventConsumer<JobChosen>
    {
        public void Handle(in EventEnvelope envelope, in JobChosen payload) =>
            bus.Publish(new Harvested(payload.CharacterId, payload.EntityId, payload.JobId, 1, 1, 401, 1, "e2e:harvest:1"), payload.CharacterId);
    }

    [Fact]
    public async Task A_level_12_character_picks_Blacksmith_at_the_job_master_keeps_it_after_a_relogin_and_cannot_pick_again()
    {
        // Standing 1.5 m south of the job master (its marker at -2200, 800; range 3 m).
        var character = await _env.CreateCharacterAsync(accountId: 540, level: 12, xCm: -2_200, yCm: 650);
        _client = new LoopbackGameClient(_server.Transport.LocalPort, _env.Ticket(_config, 540));
        var client = _client;

        Run(() => { Idle(client); return client.JobStates.Count > 0 && JobMaster(client) != 0; });
        Assert.Empty(client.JobStates[^1].Jobs);
        uint master = JobMaster(client);

        client.SendInteract(1, master, NpcInteraction.Jobs);
        Run(() => { Idle(client); return client.InteractResults.Any(r => r.Kind == NpcInteraction.Jobs); });
        Assert.Equal(OutpostReason.None, client.InteractResults.Single(r => r.Kind == NpcInteraction.Jobs).Reason);

        client.SendJob(1, master, Blacksmith, JobSlot.Main);
        client.SendJob(1, master, Blacksmith, JobSlot.Main); // a double submit
        Run(() => { Idle(client); return client.JobResults.Count > 0; });
        Assert.Equal((JobReason.None, JobSlot.Main, Blacksmith), (client.JobResults[0].Reason, client.JobResults[0].Slot, client.JobResults[0].JobNetId));
        Run(() => { Idle(client); return client.JobStates[^1].Jobs.Any(j => j.JobNetId == Blacksmith && j.Xp == 10); });
        var held = client.JobStates[^1].Jobs.Single();
        Assert.Equal((Blacksmith, JobSlot.Main, (ushort)1, 10u), (held.JobNetId, held.Slot, held.Level, held.Xp)); // the test harvest: 10 XP

        // What the save wrote: one record and one audit row, whatever the double submit did.
        var jobs = new JobRepository(_env.RealmDatabase);
        var op = Assert.Single(await jobs.OpsAsync(character.CharacterId));
        Assert.Equal(("blacksmith", JobSlot.Main, 12), (op.JobId, op.Slot, op.LevelAtPick));

        // A restart and a new login: the job is there, and a second main pick is refused.
        client.Dispose();
        _client = null;
        _server.Stop();
        _server = _env.StartServer(_config);
        _client = new LoopbackGameClient(_server.Transport.LocalPort, _env.Ticket(_config, 540));
        var again = _client;
        Run(() => { Idle(again); return again.JobStates.Count > 0 && JobMaster(again) != 0; });
        Assert.Equal((Blacksmith, JobSlot.Main), (again.JobStates[^1].Jobs.Single().JobNetId, again.JobStates[^1].Jobs.Single().Slot));
        Assert.Equal(10u, again.JobStates[^1].Jobs.Single().Xp);

        again.SendJob(1, JobMaster(again), Jeweler, JobSlot.Main);
        Run(() => { Idle(again); return again.JobResults.Count > 0; });
        Assert.Equal(JobReason.JobAlreadyChosen, again.JobResults[0].Reason);
        Assert.Single(await jobs.OpsAsync(character.CharacterId));
        Assert.Equal("blacksmith", Assert.Single((await jobs.LoadAsync(character.CharacterId)).Jobs).JobId);
    }

    /// <summary>The job master's entity id from the snapshots' spawns; 0 before it was seen.</summary>
    private static uint JobMaster(LoopbackGameClient client) =>
        client.Snapshots.SelectMany(s => s.Spawns).FirstOrDefault(s => s.Kind == EntityKind.Npc && s.DefId == JobMasterNetId).EntityId;

    private void Idle(LoopbackGameClient client)
    {
        if (client.Snapshots.Count > 0)
            client.SendFrame(MoveKeys.None, facing: 0, _config.StartingValues.InputRedundancy, InputActions.None,
                Math.Max(1, client.Snapshots[^1].Header.ServerTick - 2));
    }

    /// <summary>Calls <paramref name="step"/> about once per server tick (polling the client) until it returns true.</summary>
    private void Run(Func<bool> step)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            Assert.True(watch.Elapsed < Deadline, "Timed out waiting for the server.");
            Assert.Null(_server.WorldThread.Failure);
            _client!.Poll();
            if (_client.Connected && step())
                return;
            Thread.Sleep(1000 / _config.StartingValues.TickRateHz);
        }
    }
}
