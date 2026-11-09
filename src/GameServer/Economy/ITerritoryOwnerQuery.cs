using Shared.Simulation.Economy;

namespace GameServer.Economy;

/// <summary>The guild owning a region's territory and the rate it set for one fee type (R-4.2-05; territories are M8).</summary>
public readonly record struct TerritoryFeeOwner(long GuildId, int RatePercent);

/// <summary>
/// Who owns the territory of a region, for <see cref="FeeService"/> (spec 4.2 R-4.2-05, flow "Fee computation and split";
/// WP-M4-1). Read on the world thread. TODO(M8): the guild territories (spec 6) implement it; until then
/// <see cref="NoTerritoryOwners"/> says no region is owned, so every fee is destroyed whole.
/// </summary>
public interface ITerritoryOwnerQuery
{
    /// <summary>The owner of the region at <paramref name="regionIndex"/> (the world's region index) and its rate for <paramref name="type"/>.</summary>
    bool TryGetOwner(byte regionIndex, FeeType type, out TerritoryFeeOwner owner);
}

/// <summary>M4: no territory has an owner (TODO(M8): guild territories).</summary>
public sealed class NoTerritoryOwners : ITerritoryOwnerQuery
{
    public static readonly NoTerritoryOwners Instance = new();

    private NoTerritoryOwners()
    {
    }

    public bool TryGetOwner(byte regionIndex, FeeType type, out TerritoryFeeOwner owner)
    {
        owner = default;
        return false;
    }
}
