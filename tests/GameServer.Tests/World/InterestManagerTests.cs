using GameServer.Entities;
using GameServer.World;
using Shared.Simulation;
using static GameServer.Tests.World.SpatialGridTests;

namespace GameServer.Tests.World;

/// <summary>Spec 2.3 / R-2.3-03: per-observer interest sets with enter and leave deltas.</summary>
public class InterestManagerTests
{
    private const int Radius = 3_000;

    private readonly SpatialGrid _grid = new(1_500);

    private InterestManager Create(IVisibilityQuery? visibility = null) =>
        new(_grid, Radius, visibility ?? EveryoneVisible.Instance);

    private Player Add(uint id, int xCm, int yCm)
    {
        var player = PlayerAt(id, xCm, yCm);
        _grid.Add(player);
        return player;
    }

    private void MoveTo(Player player, int xCm, int yCm)
    {
        player.Movement = new MovementState(FixVec2.FromCm(xCm, yCm), 0);
        _grid.Update(player);
    }

    private static uint[] Ids(IEnumerable<Entity> entities) => entities.Select(e => e.Id).ToArray();

    [Fact]
    public void Entities_inside_the_square_enter_and_the_observer_sees_itself()
    {
        var observer = Add(5, 0, 0);
        Add(2, Radius, -Radius);     // corner: inside (inclusive)
        Add(9, -2_000, 1_000);
        Add(3, Radius + 1, 0);       // just outside
        Add(4, 0, 20_000);           // far away
        var interest = Create();

        interest.Update(observer);

        Assert.Equal([2u, 5u, 9u], Ids(observer.Interest.Visible));
        Assert.Equal([2u, 5u, 9u], Ids(observer.Interest.Entered));
        Assert.Empty(observer.Interest.Left);
    }

    [Fact]
    public void Nothing_changes_when_nothing_moves()
    {
        var observer = Add(1, 0, 0);
        Add(2, 100, 100);
        var interest = Create();
        interest.Update(observer);

        interest.Update(observer);

        Assert.Equal([1u, 2u], Ids(observer.Interest.Visible));
        Assert.Empty(observer.Interest.Entered);
        Assert.Empty(observer.Interest.Left);
    }

    [Fact]
    public void Moving_away_makes_entities_leave_and_others_enter()
    {
        var observer = Add(1, 0, 0);
        Add(2, 1_000, 0);
        Add(3, 7_000, 0);
        var interest = Create();
        interest.Update(observer);

        MoveTo(observer, 5_000, 0);
        interest.Update(observer);

        Assert.Equal([1u, 3u], Ids(observer.Interest.Visible));
        Assert.Equal([3u], Ids(observer.Interest.Entered));
        Assert.Equal([2u], observer.Interest.Left);
    }

    [Fact]
    public void Entities_removed_from_the_grid_leave()
    {
        var observer = Add(1, 0, 0);
        var other = Add(2, 100, 0);
        var interest = Create();
        interest.Update(observer);

        _grid.Remove(other);
        interest.Update(observer);

        Assert.Equal([2u], observer.Interest.Left);
    }

    [Fact]
    public void Entities_the_visibility_query_hides_are_never_in_interest()
    {
        // Architecture rule 9: hidden entities are never sent.
        var observer = Add(1, 0, 0);
        Add(2, 100, 0);
        Add(3, 200, 0);
        var interest = Create(new HideIds(3));

        interest.Update(observer);

        Assert.Equal([1u, 2u], Ids(observer.Interest.Visible));
    }

    [Fact]
    public void An_observer_always_sees_itself_even_if_the_query_says_no()
    {
        var observer = Add(1, 0, 0);
        var interest = Create(new HideIds(1));

        interest.Update(observer);

        Assert.Equal([1u], Ids(observer.Interest.Visible));
    }

    private sealed class HideIds(params uint[] hidden) : IVisibilityQuery
    {
        public bool CanSee(Entity observer, Entity target) => !hidden.Contains(target.Id);
    }
}
