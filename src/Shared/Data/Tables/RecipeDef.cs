using System.Collections.Generic;

namespace Shared.Data.Tables
{
    /// <summary>What a <see cref="RecipeDef"/> does (spec 5.2, 5.3). Config enum (camelCase names) and on the wire; append only.</summary>
    public enum RecipeType : byte
    {
        None = 0,

        /// <summary>Raw material to refined material (R-5.3-06: ore to ingot, rough gem to cut gem).</summary>
        Refine = 1,

        /// <summary>Tier T to tier T+1, a seeded success roll; failure consumes the inputs (R-5.2-04).</summary>
        Convert = 2,

        /// <summary>Makes a mold item (R-5.3-01 "each job creates its own molds").</summary>
        Mold = 3,
    }

    /// <summary>
    /// A forge recipe without a mold (spec 5.3 "recipe type", R-5.3-06, R-5.2-04; WP-M3-6): refine, tier conversion, or a
    /// mold-making recipe. Its fixed inputs are <see cref="RecipeInputDef"/> rows; it takes no forge actions and rolls no
    /// quality (only mold crafts do, R-5.3-02); it makes <see cref="OutputCount"/> of <see cref="OutputItemId"/>. A
    /// conversion succeeds with the <see cref="ConversionOdds"/> row of its output tier. <see cref="NetId"/> shares one
    /// namespace with <see cref="MoldDef.NetId"/>. Every number is a placeholder (balance is deferred). Catalog 8.5.
    /// </summary>
    [Table("RecipeDef", 1)]
    public sealed class RecipeDef : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Id on the wire, unique across <see cref="MoldDef"/> and <see cref="RecipeDef"/>.</summary>
        [Range(1, MoldDef.MaxNetId)] public int NetId { get; set; }

        public RecipeType Type { get; set; }

        /// <summary>Where it is made: <c>forge</c> or <c>jewelerBench</c>.</summary>
        public NpcInteraction Station { get; set; }

        [Ref(typeof(JobDef))] public string JobId { get; set; } = string.Empty;

        [Range(1, 1_000)] public int MinJobLevel { get; set; }

        /// <summary>The difficulty tier (job XP, R-5.1-04); a conversion's input tier.</summary>
        [Range(1, MaterialTierDef.MaxTier)] public int Tier { get; set; }

        [Ref(typeof(ItemDef))] public string OutputItemId { get; set; } = string.Empty;

        [Range(1, 999)] public int OutputCount { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (Type == RecipeType.None || !System.Enum.IsDefined(typeof(RecipeType), Type))
                errors.Add(nameof(Type), $"{(int)Type} is not a recipe type (refine, convert, mold)");
            if (!ForgeStations.IsStation(Station))
                errors.Add(nameof(Station), $"{Station} is not a crafting station (forge or jewelerBench)");
            if (Type == RecipeType.Mold && OutputCount != 1)
                errors.Add(nameof(OutputCount), "a mold recipe makes one mold");
        }

        /// <summary>
        /// The output fits its type (a material for refine and convert, a mold item with a <see cref="MoldDef"/> for a mold
        /// recipe, one stack), the recipe has inputs, a conversion goes from its tier to the next with an odds row, and the
        /// net id is unique among molds and recipes.
        /// </summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            var output = ItemDef.Find(set, OutputItemId);
            if (output != null)
            {
                if (OutputCount > output.MaxStack)
                    errors.Add(nameof(OutputCount), $"{OutputCount} is more than '{OutputItemId}' stacks ({output.MaxStack})");
                if ((Type == RecipeType.Refine || Type == RecipeType.Convert) && output.Category != ItemCategory.Material)
                    errors.Add(nameof(OutputItemId), $"'{OutputItemId}' is {output.Category}; a {Type} recipe makes a material");
                if (Type == RecipeType.Mold && set.TryGet(typeof(MoldDef), out _) && MoldDef.Find(set, OutputItemId) == null)
                    errors.Add(nameof(OutputItemId), $"'{OutputItemId}' has no MoldDef row; a mold recipe makes a mold");
                if (Type == RecipeType.Convert)
                {
                    if (output.Tier != Tier + 1)
                        errors.Add(nameof(OutputItemId), $"'{OutputItemId}' is tier {output.Tier}; a conversion goes from tier {Tier} to {Tier + 1} (R-5.2-04)");
                    if (set.TryGet(typeof(ConversionOdds), out _) && ConversionOdds.ForTier(set, output.Tier) == null)
                        errors.Add(nameof(OutputItemId), $"no ConversionOdds row for tier {output.Tier}");
                }
            }
            if (set.TryGet(typeof(RecipeInputDef), out _))
            {
                var inputs = Inputs(set, Id);
                if (inputs.Count == 0)
                    errors.Add(nameof(Id), "has no RecipeInputDef rows");
                if (inputs.Count > ForgeStations.MaxInputs)
                    errors.Add(nameof(Id), $"has more than {ForgeStations.MaxInputs} inputs");
                if (Type == RecipeType.Convert)
                {
                    foreach (var input in inputs)
                    {
                        var item = ItemDef.Find(set, input.ItemId);
                        if (item != null && item.Tier != Tier)
                            errors.Add(nameof(Tier), $"input '{input.ItemId}' is tier {item.Tier}; every conversion input is tier {Tier}");
                    }
                }
            }
            var settings = JobSettings.Find(set);
            if (settings != null && MinJobLevel > settings.MaxLevel)
                errors.Add(nameof(MinJobLevel), $"{MinJobLevel} is above the job level cap {settings.MaxLevel}");
            NetIds.CheckUnique<RecipeDef>(set, this, NetId, row => row.NetId, row => row.Id, errors);
        }

        public static RecipeDef? Find(ConfigSet set, string id) => NetIds.Find<RecipeDef>(set, id, row => row.Id);

        /// <summary>The recipe's input rows in file order (the order of a forge start's inputs).</summary>
        public static List<RecipeInputDef> Inputs(ConfigSet set, string recipeId)
        {
            var inputs = new List<RecipeInputDef>();
            foreach (var row in NetIds.Rows<RecipeInputDef>(set))
            {
                if (row.RecipeId == recipeId)
                    inputs.Add(row);
            }
            return inputs;
        }
    }

    /// <summary>
    /// One fixed input of a <see cref="RecipeDef"/>: <see cref="Count"/> units of a material, from one stack. Flat rows
    /// because config tables hold no object arrays. Every number is a placeholder. Catalog 8.5.
    /// </summary>
    [Table("RecipeInputDef", 1)]
    public sealed class RecipeInputDef : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        [Ref(typeof(RecipeDef))] public string RecipeId { get; set; } = string.Empty;

        [Ref(typeof(ItemDef))] public string ItemId { get; set; } = string.Empty;

        [Range(1, 999)] public int Count { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
        }

        /// <summary>The input is a material that stacks to <see cref="Count"/>, listed once per recipe.</summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            var item = ItemDef.Find(set, ItemId);
            if (item != null)
            {
                if (item.Category != ItemCategory.Material)
                    errors.Add(nameof(ItemId), $"'{ItemId}' is {item.Category}; recipe inputs are materials");
                else if (Count > item.MaxStack)
                    errors.Add(nameof(Count), $"{Count} is more than '{ItemId}' stacks ({item.MaxStack})");
            }
            foreach (var other in NetIds.Rows<RecipeInputDef>(set))
            {
                if (ReferenceEquals(other, this))
                    break;
                if (other.RecipeId == RecipeId && other.ItemId == ItemId)
                {
                    errors.Add(nameof(ItemId), $"'{ItemId}' is already an input of '{RecipeId}' ('{other.Id}')");
                    break;
                }
            }
        }
    }
}
