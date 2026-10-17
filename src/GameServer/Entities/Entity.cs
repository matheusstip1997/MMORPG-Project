using Shared.Protocol;
using Shared.Simulation;

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

    /// <summary>Height in cm. Movement is 2D; TODO(C2): height from the exported heightmap.</summary>
    public int HeightCm { get; set; }

    /// <summary>Ground-plane X in whole cm (MovementSim axes).</summary>
    public int XCm => FixedPoint.ToCmRound(Movement.Position.X);

    /// <summary>Ground-plane Y in whole cm (MovementSim axes: +Y = screen-up). Snapshots send it as their Z.</summary>
    public int YCm => FixedPoint.ToCmRound(Movement.Position.Y);

    /// <summary>Grid cell key while the entity is registered in a <see cref="GameServer.World.SpatialGrid"/>.</summary>
    internal long GridCell { get; set; }

    internal bool InGrid { get; set; }

    public override string ToString() => $"{Kind} #{Id} at ({XCm}, {YCm}) height {HeightCm} cm";
}
