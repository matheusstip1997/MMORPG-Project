#nullable enable
using System;
using Shared.Simulation.Actions;

namespace Game.Gameplay.Dev
{
    /// <summary>One entity's action state at one tick, as the timeline stores it.</summary>
    public readonly struct TimelineSample
    {
        public TimelineSample(uint tick, ActionState state, int actionNetId, int elapsed, bool mismatch)
        {
            Tick = tick;
            State = state;
            ActionNetId = actionNetId;
            Elapsed = elapsed;
            Mismatch = mismatch;
        }

        /// <summary>The clock of its row: client ticks for the prediction, server ticks for the server and the target.</summary>
        public uint Tick { get; }

        public ActionState State { get; }

        /// <summary><c>ActionDef.NetId</c> of the running action; 0 when none.</summary>
        public int ActionNetId { get; }

        /// <summary>Ticks since the action or interrupt started.</summary>
        public int Elapsed { get; }

        /// <summary>Server rows: reconciliation found the predicted action state different from the server's (spec 2.2).</summary>
        public bool Mismatch { get; }
    }

    /// <summary>
    /// The action timeline of the dev scene (spec 11.7, backlog J): the last <c>DevTools.timelineTicks</c> samples of three
    /// rows, each on its own clock: the local player's predicted state per client tick, the server's acknowledged state of
    /// the local player per own snapshot (with the reconciliation's verdict), and the target's state per snapshot. Rings
    /// filled in place; nothing allocates after construction. Engine-free, so dotnet tests run it.
    /// </summary>
    public sealed class ActionTimelineModel
    {
        private readonly Ring _predicted;
        private readonly Ring _server;
        private readonly Ring _target;

        public ActionTimelineModel(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            _predicted = new Ring(capacity);
            _server = new Ring(capacity);
            _target = new Ring(capacity);
        }

        public int Capacity => _predicted.Capacity;

        /// <summary>Server samples whose action state the prediction got wrong, since <see cref="Clear"/>.</summary>
        public long Mismatches { get; private set; }

        /// <summary>The entity the target row follows; 0 for none.</summary>
        public uint TargetId { get; private set; }

        public int PredictedCount => _predicted.Count;
        public int ServerCount => _server.Count;
        public int TargetCount => _target.Count;

        /// <summary>The <paramref name="age"/>-th newest predicted sample (0 = newest).</summary>
        public TimelineSample Predicted(int age) => _predicted.Get(age);
        public TimelineSample Server(int age) => _server.Get(age);
        public TimelineSample Target(int age) => _target.Get(age);

        /// <summary>Once per predicted client tick.</summary>
        public void AddPredicted(uint clientTick, ActionMachineState state) =>
            _predicted.Add(new TimelineSample(clientTick, state.State, NetIdOf(state), state.Elapsed, false));

        /// <summary>Once per snapshot that carried the local player.</summary>
        public void AddServer(uint serverTick, ActionState state, int actionNetId, int age, bool mismatch)
        {
            if (_server.Count > 0 && _server.Get(0).Tick == serverTick)
                return;
            _server.Add(new TimelineSample(serverTick, state, actionNetId, age, mismatch));
            if (mismatch)
                Mismatches++;
        }

        /// <summary>Once per snapshot for the followed target; a new target starts its row over.</summary>
        public void AddTarget(uint entityId, uint serverTick, ActionState state, int actionNetId, int age)
        {
            if (entityId != TargetId)
            {
                TargetId = entityId;
                _target.Clear();
            }
            if (_target.Count > 0 && _target.Get(0).Tick == serverTick)
                return;
            _target.Add(new TimelineSample(serverTick, state, actionNetId, age, false));
        }

        public void Clear()
        {
            _predicted.Clear();
            _server.Clear();
            _target.Clear();
            Mismatches = 0;
            TargetId = 0;
        }

        private static int NetIdOf(ActionMachineState state) => state.Action?.NetId ?? 0;

        private sealed class Ring
        {
            private readonly TimelineSample[] _items;
            private int _next;

            public Ring(int capacity)
            {
                _items = new TimelineSample[capacity];
            }

            public int Capacity => _items.Length;

            public int Count { get; private set; }

            public void Add(in TimelineSample sample)
            {
                _items[_next] = sample;
                _next = (_next + 1) % _items.Length;
                if (Count < _items.Length)
                    Count++;
            }

            public TimelineSample Get(int age)
            {
                if ((uint)age >= (uint)Count)
                    throw new ArgumentOutOfRangeException(nameof(age));
                return _items[(_next - 1 - age + _items.Length * 2) % _items.Length];
            }

            public void Clear()
            {
                _next = 0;
                Count = 0;
            }
        }
    }
}
