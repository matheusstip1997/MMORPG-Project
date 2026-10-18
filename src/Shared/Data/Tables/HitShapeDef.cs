namespace Shared.Data.Tables
{
    /// <summary>The geometry of a hit shape (R-3.1-03). Config enum (camelCase names); append only.</summary>
    public enum HitShapeKind : byte
    {
        /// <summary>A circular sector: apex at the shape origin, axis forward, <c>rangeCm</c> and <c>halfAngleDeg</c>.</summary>
        Cone = 1,
        /// <summary>A rectangle from the shape origin forward: <c>lengthCm</c> forward, <c>widthCm</c> across (centered).</summary>
        Box = 2,
        /// <summary>
        /// Every point within <c>radiusCm</c> of the segment from the shape origin <c>lengthCm</c> forward, so it reaches
        /// <c>radiusCm</c> behind the origin and past the far end. Length 0 is a circle around the origin.
        /// </summary>
        Capsule = 3,
    }

    /// <summary>
    /// One hit shape of a <c>Strike</c> or <c>GroundArea</c> effect (spec 3.1 <c>HitShapeDef</c>, R-3.1-03). Config rows hold
    /// no nested objects, so an effect with several shapes has several rows; a target inside any of them is hit once per
    /// effect. The shape is placed when the effect fires, relative to the caster's position and the facing locked at the
    /// start of the action (R-3.1-01): its origin is <see cref="ForwardOffsetCm"/> along the facing and
    /// <see cref="SideOffsetCm"/> to the right of it, and its axis points along the facing. Targets are circles (position
    /// and radius); touching counts as a hit. A size field the kind does not use must be 0.
    /// <list type="bullet">
    /// <item>Cone: <see cref="RangeCm"/>, <see cref="HalfAngleDeg"/> (180 is a full circle).</item>
    /// <item>Box: <see cref="LengthCm"/>, <see cref="WidthCm"/>.</item>
    /// <item>Capsule: <see cref="LengthCm"/> (0 or more), <see cref="RadiusCm"/>.</item>
    /// </list>
    /// Geometry: <c>Shared.Simulation.Combat.HitShape</c>.
    /// </summary>
    [Table("HitShapeDef", 1)]
    public sealed class HitShapeDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Shapes per effect HitResolver evaluates (a fixed bound for the per-effect loop).</summary>
        public const int MaxShapesPerEffect = 4;

        [TableId] public string Id { get; set; } = string.Empty;

        [Ref(typeof(ActionEffectDef))] public string EffectId { get; set; } = string.Empty;

        public HitShapeKind Kind { get; set; }

        /// <summary>Along the locked facing; negative is behind the caster.</summary>
        [Range(-2_000, 2_000)] public int ForwardOffsetCm { get; set; }

        /// <summary>To the right of the locked facing; negative is to the left.</summary>
        [Range(-2_000, 2_000)] public int SideOffsetCm { get; set; }

        [Range(0, 5_000)] public int RangeCm { get; set; }
        [Range(0, 180)] public int HalfAngleDeg { get; set; }
        [Range(0, 5_000)] public int LengthCm { get; set; }
        [Range(0, 5_000)] public int WidthCm { get; set; }
        [Range(0, 2_500)] public int RadiusCm { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            switch (Kind)
            {
                case HitShapeKind.Cone:
                    Require(errors, nameof(RangeCm), RangeCm, "a cone needs a range");
                    Require(errors, nameof(HalfAngleDeg), HalfAngleDeg, "a cone needs a half angle");
                    Unused(errors, length: true, width: true, radius: true);
                    break;
                case HitShapeKind.Box:
                    Require(errors, nameof(LengthCm), LengthCm, "a box needs a length");
                    Require(errors, nameof(WidthCm), WidthCm, "a box needs a width");
                    Unused(errors, range: true, halfAngle: true, radius: true);
                    break;
                case HitShapeKind.Capsule:
                    Require(errors, nameof(RadiusCm), RadiusCm, "a capsule needs a radius");
                    Unused(errors, range: true, halfAngle: true, width: true);
                    break;
                default:
                    errors.Add(nameof(Kind), "unknown shape kind");
                    break;
            }
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (!set.TryGet(typeof(ActionEffectDef), out var effects))
                return;
            foreach (var row in effects.Rows)
            {
                var effect = (ActionEffectDef)row;
                if (effect.Id != EffectId)
                    continue;
                // R-3.1-03: shapes resolve strikes; spec 3.1: a ground area has a shape. Other kinds have none.
                if (!TakesShapes(effect.Kind))
                    errors.Add(nameof(EffectId), $"'{EffectId}' is a {effect.Kind}; only Strike and GroundArea effects have hit shapes");
                break;
            }

            int count = CountFor(set, EffectId);
            if (count > MaxShapesPerEffect)
                errors.Add(nameof(EffectId), $"'{EffectId}' has {count} hit shapes, at most {MaxShapesPerEffect}");
        }

        /// <summary>Effect kinds resolved from hit shapes (R-3.1-03, spec 3.1).</summary>
        public static bool TakesShapes(EffectKind kind) => kind == EffectKind.Strike || kind == EffectKind.GroundArea;

        /// <summary>How many rows of the loaded <c>HitShapeDef</c> table belong to the effect, or -1 if the table is not loaded.</summary>
        public static int CountFor(ConfigSet set, string effectId)
        {
            if (!set.TryGet(typeof(HitShapeDef), out var table))
                return -1;
            int count = 0;
            foreach (var row in table.Rows)
            {
                if (((HitShapeDef)row).EffectId == effectId)
                    count++;
            }
            return count;
        }

        private static void Require(RowErrors errors, string field, int value, string message)
        {
            if (value <= 0)
                errors.Add(field, message);
        }

        private void Unused(RowErrors errors, bool range = false, bool halfAngle = false, bool length = false,
            bool width = false, bool radius = false)
        {
            string kind = Kind.ToString();
            if (range && RangeCm != 0) errors.Add(nameof(RangeCm), $"must be 0 for {kind}");
            if (halfAngle && HalfAngleDeg != 0) errors.Add(nameof(HalfAngleDeg), $"must be 0 for {kind}");
            if (length && LengthCm != 0) errors.Add(nameof(LengthCm), $"must be 0 for {kind}");
            if (width && WidthCm != 0) errors.Add(nameof(WidthCm), $"must be 0 for {kind}");
            if (radius && RadiusCm != 0) errors.Add(nameof(RadiusCm), $"must be 0 for {kind}");
        }
    }
}
