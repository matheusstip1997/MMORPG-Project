using System;

namespace Shared.Simulation
{
    /// <summary>One body that may block a move (R-2.2-07): a circle of <c>bodyRadiusCm</c> around an entity's position.</summary>
    public readonly struct BodyCircle
    {
        public BodyCircle(uint id, FixVec2 center, int radiusRaw)
        {
            Id = id;
            Center = center;
            RadiusRaw = radiusRaw;
        }

        public uint Id { get; }

        /// <summary>Raw Q24.8 position.</summary>
        public FixVec2 Center { get; }

        /// <summary>Raw Q24.8 radius.</summary>
        public int RadiusRaw { get; }
    }

    /// <summary>
    /// The bodies one move is resolved against, kept in entity-id order (rule 6: the same set gives the same result on the
    /// server and in prediction, whatever order it was gathered in). Reused across moves; it grows by doubling, so it stops
    /// allocating once it has seen the largest crowd.
    /// </summary>
    public sealed class BodyList
    {
        private BodyCircle[] _items;

        public BodyList(int capacity = 16)
        {
            _items = new BodyCircle[Math.Max(1, capacity)];
        }

        public int Count { get; private set; }

        public BodyCircle this[int index] =>
            index >= 0 && index < Count ? _items[index] : throw new ArgumentOutOfRangeException(nameof(index));

        public void Clear() => Count = 0;

        /// <summary>Adds a body at its id's place (an insertion sort: gathers are small and mostly in order already).</summary>
        public void Add(uint id, FixVec2 center, int radiusRaw)
        {
            if (radiusRaw < 0)
                throw new ArgumentOutOfRangeException(nameof(radiusRaw));
            if (Count == _items.Length)
                Array.Resize(ref _items, _items.Length * 2);
            int i = Count;
            while (i > 0 && _items[i - 1].Id > id)
            {
                _items[i] = _items[i - 1];
                i--;
            }
            _items[i] = new BodyCircle(id, center, radiusRaw);
            Count++;
        }
    }
}
