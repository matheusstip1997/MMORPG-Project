using GameServer.Entities;
using GameServer.Net;
using GameServer.World;
using Shared.Simulation;

namespace GameServer.Tests.World;

/// <summary>Spec 2.3: uniform cells, each entity registered in the cell holding its position.</summary>
public class SpatialGridTests
{
    private const int Cell = 1_500;

    internal static Player PlayerAt(uint id, int xCm, int yCm) =>
        new(id, peerId: (int)id, accountId: id, new InputQueue(4), new MovementState(FixVec2.FromCm(xCm, yCm), 0), heightCm: 0);

    private static List<uint> Query(SpatialGrid grid, int minX, int minY, int maxX, int maxY)
    {
        var results = new List<Entity>();
        grid.QueryCells(minX, minY, maxX, maxY, results);
        return results.Select(e => e.Id).Order().ToList();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1_499, 0)]
    [InlineData(1_500, 1)]
    [InlineData(-1, -1)]
    [InlineData(-1_500, -1)]
    [InlineData(-1_501, -2)]
    public void Cells_round_toward_negative_infinity(int cm, int cell)
    {
        Assert.Equal(cell, new SpatialGrid(Cell).CellOf(cm));
    }

    [Fact]
    public void Query_returns_entities_of_overlapping_cells_only()
    {
        var grid = new SpatialGrid(Cell);
        grid.Add(PlayerAt(1, 100, 100));
        grid.Add(PlayerAt(2, 1_600, 100));
        grid.Add(PlayerAt(3, -100, -100));
        grid.Add(PlayerAt(4, 10_000, 10_000));

        Assert.Equal([1u, 2u, 3u], Query(grid, -10, -10, 1_510, 10));
        Assert.Equal([1u], Query(grid, 0, 0, 1_499, 1_499));
    }

    [Fact]
    public void Update_moves_an_entity_to_its_new_cell_and_remove_forgets_it()
    {
        var grid = new SpatialGrid(Cell);
        var player = PlayerAt(1, 100, 100);
        grid.Add(player);

        player.Movement = new MovementState(FixVec2.FromCm(5_000, 100), 0);
        grid.Update(player);

        Assert.Empty(Query(grid, 0, 0, 1_499, 1_499));
        Assert.Equal([1u], Query(grid, 4_500, 0, 5_999, 1_499));

        grid.Remove(player);
        Assert.Empty(Query(grid, 4_500, 0, 5_999, 1_499));
        Assert.False(player.InGrid);
    }
}
