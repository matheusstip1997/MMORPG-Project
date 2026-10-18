using GameServer.Combat;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;

namespace GameServer.Entities;

/// <summary>
/// Anything that lives in a <see cref="GameServer.World.GameWorld"/> (the World of spec 1.5, 2.3). Owned by its simulation
/// thread: nothing else reads or writes it. Phases process entities in <see cref="Id"/> order (spec 1.6).
/// </summary>
public abstract class Entity
{
    protected Entity(uint id, EntityKind kind, MovementState movement, int heightCm)
    {
        if (id == 0)
            throw new ArgumentOutOfRangeException(nameof(id), "0 is not a valid entity id");
        Id = id;
        Kind = kind;
        Movement = movement;
        HeightCm = heightCm;
    }

    public uint Id { get; }

    public EntityKind Kind { get; }

    /// <summary>Ground position (Q24.8 cm) and facing, advanced by <see cref="MovementSim"/>.</summary>
    public MovementState Movement { get; set; }

    /// <summary>
    /// Height in cm (the vertical axis of snapshots). Movement is 2D; phase 2 sets it from the exported grid
    /// (one walkable layer in M1; overlapping surfaces are TODO(open) in <see cref="Shared.World.WorldGridFormat"/>).
    /// </summary>
    public int HeightCm { get; set; }

    /// <summary>Ground-plane X in whole cm (MovementSim axes).</summary>
    public int XCm => FixedPoint.ToCmRound(Movement.Position.X);

    /// <summary>Ground-plane Y in whole cm (MovementSim axes: +Y = screen-up). Snapshots send it as their Z.</summary>
    public int YCm => FixedPoint.ToCmRound(Movement.Position.Y);

    /// <summary>Grid cell key while the entity is registered in a <see cref="GameServer.World.SpatialGrid"/>.</summary>
    internal long GridCell { get; set; }

    internal bool InGrid { get; set; }

    /// <summary>
    /// Index of the region the entity stands in (spec 2.3 per-cell region lookup), 0 for none; resolve it with
    /// <see cref="GameServer.World.Map.WorldMap.RegionByIndex"/>. Set at spawn and in phase 2.
    /// </summary>
    public byte RegionIndex { get; internal set; }

    /// <summary>
    /// Position the spec 2.2 speed check measured last (end of the previous phase 2, or the spawn, or the end of a dash
    /// the server moved in phase 3).
    /// </summary>
    internal FixVec2 SpeedCheckedPosition { get; set; }

    /// <summary>
    /// The entity's action state machine (spec 3.1, backlog D2), run in phase 3. The world gives it its catalog at spawn;
    /// until then it has no actions.
    /// </summary>
    public ActionMachineState Actions { get; internal set; } = new(ActionCatalog.Empty);

    /// <summary>
    /// Stats, HP/MP/stamina, faction, poise, flinch and position history (backlog D5 wiring); null for an entity that
    /// cannot fight, or a player in a world built without <see cref="GameServer.Combat.CombatRules"/>.
    /// </summary>
    public Combatant? Combatant { get; internal set; }

    /// <summary>
    /// Definition id its spawn events carry (spec 1.7), so clients pick its look: <c>ClassDef.netId</c> for a player,
    /// <c>MonsterDef.netId</c> for a monster; 0 when it has none.
    /// </summary>
    public ushort DefId { get; internal set; }

    public override string ToString() => $"{Kind} #{Id} at ({XCm}, {YCm}) height {HeightCm} cm";
}
