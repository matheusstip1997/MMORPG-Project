using GameServer.Entities;

namespace GameServer.World;

/// <summary>
/// Uniform grid over the ground plane (spec 2.3): every entity is registered in the cell holding its position.
/// Cell size is <c>StartingValuesM1.GridCellCm</c>. The map has no fixed size yet (R-2.3-01), so cells live in a
/// dictionary and are created on first use; a cell is kept once created, so moving around allocates nothing.
/// Simulation thread only.
/// </summary>
public sealed class SpatialGrid
{
    private readonly int _cellCm;
    private readonly Dictionary<long, List<Entity>> _cells = new();

    public SpatialGrid(int cellCm)
    {
        if (cellCm <= 0)
            throw new ArgumentOutOfRangeException(nameof(cellCm), "must be positive");
        _cellCm = cellCm;
    }

    public int CellCm => _cellCm;

    public void Add(Entity entity)
    {
        if (entity.InGrid)
            throw new InvalidOperationException($"{entity} is already in the grid.");
        long cell = CellKey(entity.XCm, entity.YCm);
        CellList(cell).Add(entity);
        entity.GridCell = cell;
        entity.InGrid = true;
    }

    public void Remove(Entity entity)
    {
        if (!entity.InGrid)
            return;
        _cells[entity.GridCell].Remove(entity);
        entity.InGrid = false;
    }

    /// <summary>Call after the entity's position changed; moves it to its new cell if it crossed a border.</summary>
    public void Update(Entity entity)
    {
        if (!entity.InGrid)
            throw new InvalidOperationException($"{entity} is not in the grid.");
        long cell = CellKey(entity.XCm, entity.YCm);
        if (cell == entity.GridCell)
            return;
        _cells[entity.GridCell].Remove(entity);
        CellList(cell).Add(entity);
        entity.GridCell = cell;
    }

    /// <summary>
    /// Adds every entity in a cell that overlaps the rectangle (inclusive, cm) to <paramref name="results"/>.
    /// Entities near the border of those cells can lie outside the rectangle: callers test the exact shape.
    /// </summary>
    public void QueryCells(int minXCm, int minYCm, int maxXCm, int maxYCm, List<Entity> results)
    {
        int minCellX = CellOf(minXCm), maxCellX = CellOf(maxXCm);
        int minCellY = CellOf(minYCm), maxCellY = CellOf(maxYCm);
        for (int cx = minCellX; cx <= maxCellX; cx++)
        {
            for (int cy = minCellY; cy <= maxCellY; cy++)
            {
                if (_cells.TryGetValue(Key(cx, cy), out var list))
                    results.AddRange(list);
            }
        }
    }

    /// <summary>Cell index along one axis; rounds toward negative infinity so cell 0 is [0, CellCm).</summary>
    public int CellOf(int cm) => FloorDiv(cm, _cellCm);

    private long CellKey(int xCm, int yCm) => Key(CellOf(xCm), CellOf(yCm));

    private static long Key(int cellX, int cellY) => ((long)cellX << 32) | (uint)cellY;

    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }

    private List<Entity> CellList(long cell)
    {
        if (!_cells.TryGetValue(cell, out var list))
        {
            list = new List<Entity>();
            _cells.Add(cell, list);
        }
        return list;
    }
}
