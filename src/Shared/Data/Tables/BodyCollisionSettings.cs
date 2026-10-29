namespace Shared.Data.Tables
{
    /// <summary>
    /// What a move that would enter another body does (R-2.2-07). Config enum (camelCase names); append only.
    /// </summary>
    public enum BodyResponse : byte
    {
        /// <summary>The move ends at contact.</summary>
        Stop = 0,

        /// <summary>The move stops at contact, then the rest of it goes on along the body's tangent (walks round it).</summary>
        Slide = 1,
    }

    /// <summary>
    /// Body collision (R-2.2-07, user decision 2026-10-27; spec 2.2 "Body collision (M1)"): living monsters and bosses block
    /// movement, players pass through each other. A body is the circle of <c>bodyRadiusCm</c> (<see cref="ClassDef"/>,
    /// <see cref="MonsterDef"/>) that hits use. The server and client prediction run the same shared code
    /// (<c>Shared/Simulation/BodyCollision</c>) with these values. Placeholders in data/tables.
    /// </summary>
    [Table("BodyCollisionSettings", 1)]
    [Singleton]
    public sealed class BodyCollisionSettings : IValidatableRow
    {
        /// <summary>A walk (players' move keys, monsters' chase and walk home) that meets a body: slide round it, or stop.</summary>
        public BodyResponse WalkResponse { get; set; } = BodyResponse.Slide;

        /// <summary>
        /// A Dash effect (the dodge, the Archer's Dash and Back Step, Jump Strike, a monster's pounce) that meets a body: stop
        /// (the dash ends early; its timing and i-frames are unchanged), or slide.
        /// </summary>
        public BodyResponse DashResponse { get; set; } = BodyResponse.Stop;

        /// <summary>
        /// Two bodies may overlap this much at contact: absorbs the whole-cm snapshot rounding client prediction works with.
        /// Below the smallest body radius (cross-table rule in <c>CombatRules.TryBuild</c>).
        /// </summary>
        [Range(0, 50)] public int OverlapToleranceCm { get; set; }

        /// <summary>How many times one walk step may slide on after a contact before it stops (a slide into a second body).</summary>
        [Range(1, 4)] public int MaxSlideIterations { get; set; }

        public void Validate(RowErrors errors)
        {
            if (WalkResponse != BodyResponse.Stop && WalkResponse != BodyResponse.Slide)
                errors.Add(nameof(WalkResponse), "unknown body response");
            if (DashResponse != BodyResponse.Stop && DashResponse != BodyResponse.Slide)
                errors.Add(nameof(DashResponse), "unknown body response");
        }
    }
}
