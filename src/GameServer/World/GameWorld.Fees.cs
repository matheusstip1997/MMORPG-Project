using GameServer.Economy;
using GameServer.Events;

namespace GameServer.World;

/// <summary>
/// Fees in the world (spec 4.2 R-4.2-04/05, WP-M4-1): one <see cref="FeeService"/>, the only fee computer, for the systems that
/// owe fees (listings WP-M4-7, stalls WP-M4-8, contracts WP-M4-10). It hears the inventory's commits to announce
/// <c>FeeCharged</c>. Without the fee table or the items, no fees (worlds built for tests).
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>The fee computer (WP-M4-1); null without the <c>FeeRates</c> table or the items.</summary>
    public FeeService? Fees { get; private set; }

    private void BuildFees(EconomyRules economy, EventBus? events)
    {
        if (_inventory == null || economy.Fees is not { } rates)
            return;
        // TODO(M8): the guild territories answer who owns a region; until then every fee is destroyed whole.
        Fees = new FeeService(rates, _inventory, NoTerritoryOwners.Instance, events) { Jobs = _jobs };
        _inventory.OpCommitted += Fees.OnOpCommitted;
    }
}
