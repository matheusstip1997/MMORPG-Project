using Shared.Data.Tables;
using Shared.Simulation.Crafting;
using Shared.Simulation.Items;

namespace GameServer.Crafting;

/// <summary>
/// Where a forge session is (spec 5.3: <c>Created → MaterialsReserved → Running → Finished|Cancelled</c>; WP-M3-6).
/// <see cref="Finishing"/> is the wait for the save that commits the result (rule 5): the inputs are consumed and the
/// output made in memory, nothing is shown yet.
/// </summary>
public enum ForgeSessionState : byte
{
    Created = 0,
    MaterialsReserved = 1,
    Running = 2,
    Finishing = 3,
    Finished = 4,
    Cancelled = 5,
}

/// <summary>
/// One forge session of a character (spec 5.3, R-5.3-01..03; WP-M3-6), on the world thread. It reads its inputs only from
/// its reservation (<see cref="ReservationKey"/>; spec 5.3 "the session reads inputs from reserved/escrowed items"), and
/// keeps the owner apart from the character that gets the output, so a contract can own a session and escrow can supply
/// its inputs later (spec 5.4).
/// </summary>
public sealed class ForgeSession
{
    internal ForgeSession(long owner, ForgeDefinition definition, uint stationEntityId, NpcInteraction station, ulong seed,
        string reservationKey, InventoryLocation[] inputs, ItemDef[] inputItems, byte[] inputRarities, bool[] inputStolen,
        InventoryLocation? mold, int connection)
    {
        Owner = owner;
        OutputRecipient = owner;
        Definition = definition;
        StationEntityId = stationEntityId;
        Station = station;
        Seed = seed;
        ReservationKey = reservationKey;
        Inputs = inputs;
        InputItems = inputItems;
        InputRarities = inputRarities;
        InputStolen = inputStolen;
        Mold = mold;
        Connection = connection;
    }

    /// <summary>The character that runs the forge (its job and level count). TODO(M4): a contract owns the session (spec 5.4).</summary>
    public long Owner { get; }

    /// <summary>The character that gets the output. TODO(M4): the contract's customer (spec 5.4 "the item goes straight to the customer").</summary>
    public long OutputRecipient { get; }

    public ForgeDefinition Definition { get; }

    public uint StationEntityId { get; }

    public NpcInteraction Station { get; }

    /// <summary>The seed of every random number of the session, drawn at Finish (rule 6; logged in <c>craft_ops</c> and telemetry).</summary>
    public ulong Seed { get; }

    /// <summary>The inventory reservation holding the inputs, also the operation key of the result.</summary>
    public string ReservationKey { get; }

    /// <summary>The mold's own reservation: released at the end, never consumed (R-5.3-01 "molds are reusable").</summary>
    public string MoldKey => ReservationKey + ":mold";

    /// <summary>
    /// The reserved input stacks, one per mold slot or recipe input. TODO(M4): escrowed inputs come from the contract's
    /// escrow instead of the owner's bag (spec 5.4); the session reads them only through the reservation either way.
    /// </summary>
    public IReadOnlyList<InventoryLocation> Inputs { get; }

    /// <summary>What each input holds, read when it was reserved (the reservation keeps it from changing).</summary>
    public IReadOnlyList<ItemDef> InputItems { get; }

    public IReadOnlyList<byte> InputRarities { get; }

    public IReadOnlyList<bool> InputStolen { get; }

    /// <summary>The reserved mold in the Safe bag; null for a recipe.</summary>
    public InventoryLocation? Mold { get; }

    /// <summary>The connection that started it (its answers go there only).</summary>
    public int Connection { get; }

    public ForgeSessionState State { get; internal set; } = ForgeSessionState.Created;

    /// <summary>The forge actions used, in order (<c>ForgeActionDef.netId</c>).</summary>
    public List<int> Actions { get; } = new();

    /// <summary>The job tree's quality term, read once at the start (job-tree decision 10), already capped by <c>QualityCaps</c> jobTree.</summary>
    public int QualityBonus { get; internal init; }

    /// <summary>Forge actions beyond <c>ForgeSettings.maxActions</c> (the job tree's actionCount), read at the start.</summary>
    public int ExtraActions { get; internal init; }

    /// <summary>Percentage points added to a conversion's success, read at the start.</summary>
    public int ConversionBonus { get; internal init; }

    /// <summary>The chance a failed conversion keeps its inputs, read at the start.</summary>
    public int KeepInputsPercent { get; internal init; }

    /// <summary>The rounds of the job's forge challenge (WP-M3-10); null when the job plays none or the definition allows no round.</summary>
    public ForgeChallengeRun? Challenge { get; internal set; }

    /// <summary>The finish request waiting for its save, 0 before.</summary>
    public uint FinishRequestId { get; internal set; }

    /// <summary>The connection that sent <see cref="FinishRequestId"/>.</summary>
    public int FinishConnection { get; internal set; }
}
