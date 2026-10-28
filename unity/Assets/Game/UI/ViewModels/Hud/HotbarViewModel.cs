#nullable enable
using System;
using Game.UI.Config;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Game.UI.ViewModels.Hud
{
    /// <summary>One hotbar slot (spec 9.6): the action on it and its cooldown and flash end times (display clock, ms).</summary>
    public sealed class HotbarSlotView
    {
        internal HotbarSlotView(int skillSlot)
        {
            SkillSlot = skillSlot;
        }

        /// <summary>The <c>ActionBindings</c> skill slot (1-6 keys 1-6, 7 Q, 8 E, 9 R, 10 F, 11 Z).</summary>
        public int SkillSlot { get; }

        /// <summary>The bound action's id; empty for an empty slot.</summary>
        public string ActionId { get; internal set; } = "";

        /// <summary>When the cooldown ends; 0 when ready.</summary>
        public double CooldownEndMs { get; internal set; }

        public double CooldownTotalMs { get; internal set; }

        /// <summary>The refused-press flash lasts until this time (spec 9.9 hotbar flow); 0 for none.</summary>
        public double FlashUntilMs { get; internal set; }

        /// <summary>
        /// R-3.5-07 (WP-M2-5): the skill needs a weapon type the equipped set does not have: it stays on the bar, greyed, and a
        /// press is refused with <c>WrongWeapon</c>.
        /// </summary>
        public bool Unusable { get; internal set; }

        /// <summary>The skill-tree node on the slot (WP-M2-5); empty for none (the hotbar names nodes, the loadout actions).</summary>
        public string NodeId { get; internal set; } = "";

        public bool IsEmpty => ActionId.Length == 0;

        /// <summary>Cooldown left at <paramref name="nowMs"/>, 0 when ready.</summary>
        public double CooldownLeftMs(double nowMs) => CooldownEndMs <= 0 ? 0 : Math.Max(0.0, CooldownEndMs - nowMs);

        /// <summary>Share of the cooldown still to run, 1 just after use, 0 when ready (the overlay height).</summary>
        public float CooldownFraction(double nowMs) =>
            CooldownTotalMs <= 0 ? 0f : (float)Math.Min(1.0, CooldownLeftMs(nowMs) / CooldownTotalMs);
    }

    /// <summary>
    /// The hotbar (spec 9.3 <c>hotbar_left</c>, <c>hotbar_right</c>, 9.6): the class's actions by skill slot, with
    /// cooldowns computed locally from timestamps (spec 9.9). Cooldown values come from the predicted action machine; the
    /// view model raises <see cref="ViewModel.Changed"/> only when an action changes, a cooldown starts or its end moves by
    /// more than the tolerance, or a slot flashes. Updates between <see cref="BeginUpdate"/> and <see cref="EndUpdate"/>
    /// raise it once.
    /// </summary>
    public sealed class HotbarViewModel : ViewModel
    {
        private readonly HotbarSlotView[] _slots = new HotbarSlotView[ActionBindings.MaxSkillSlots + 1];
        private int _batch;
        private bool _dirty;

        public HotbarViewModel()
        {
            for (int i = 1; i < _slots.Length; i++)
                _slots[i] = new HotbarSlotView(i);
        }

        /// <summary>The class whose bindings fill the slots; empty before the own spawn named it.</summary>
        public string ClassId { get; private set; } = "";

        /// <summary>A slot by skill slot number (1 to <see cref="ActionBindings.MaxSkillSlots"/>), or null outside that range.</summary>
        public HotbarSlotView? Slot(int skillSlot) => skillSlot > 0 && skillSlot < _slots.Length ? _slots[skillSlot] : null;

        public void BeginUpdate() => _batch++;

        public void EndUpdate()
        {
            if (--_batch == 0 && _dirty)
            {
                _dirty = false;
                NotifyChanged();
            }
        }

        public void SetClass(string classId)
        {
            if (classId == ClassId)
                return;
            ClassId = classId;
            Mark();
        }

        public void SetAction(int skillSlot, string actionId)
        {
            var slot = Slot(skillSlot);
            if (slot == null || slot.ActionId == actionId)
                return;
            slot.ActionId = actionId;
            slot.CooldownEndMs = 0;
            slot.CooldownTotalMs = 0;
            Mark();
        }

        /// <summary>The node and weapon state of a slot (WP-M2-5, R-3.5-07): who is on it, and whether it is greyed.</summary>
        public void SetSkill(int skillSlot, string nodeId, bool unusable)
        {
            var slot = Slot(skillSlot);
            if (slot == null || (slot.NodeId == nodeId && slot.Unusable == unusable))
                return;
            slot.NodeId = nodeId;
            slot.Unusable = unusable;
            Mark();
        }

        /// <summary>
        /// The predicted cooldown of a slot at <paramref name="nowMs"/>: <paramref name="leftMs"/> to go of
        /// <paramref name="totalMs"/>. A cooldown end within <paramref name="toleranceMs"/> of the stored one is the same
        /// cooldown (the prediction counts in ticks, the display in frames).
        /// </summary>
        public void SetCooldown(int skillSlot, double nowMs, double leftMs, double totalMs, double toleranceMs)
        {
            var slot = Slot(skillSlot);
            if (slot == null)
                return;
            double end = leftMs > 0 ? nowMs + leftMs : 0;
            if ((end == 0) == (slot.CooldownEndMs == 0) && Math.Abs(end - slot.CooldownEndMs) <= toleranceMs
                && totalMs == slot.CooldownTotalMs)
                return;
            slot.CooldownEndMs = end;
            slot.CooldownTotalMs = end == 0 ? 0 : totalMs;
            Mark();
        }

        /// <summary>The server refused a press of this slot (spec 9.9: show the reason, flash the slot).</summary>
        public void Flash(int skillSlot, double nowMs, double durationMs)
        {
            var slot = Slot(skillSlot);
            if (slot == null)
                return;
            slot.FlashUntilMs = nowMs + durationMs;
            Mark();
        }

        public void Clear()
        {
            BeginUpdate();
            SetClass("");
            for (int i = 1; i < _slots.Length; i++)
            {
                SetAction(i, "");
                SetSkill(i, "", false);
                if (_slots[i].FlashUntilMs != 0)
                {
                    _slots[i].FlashUntilMs = 0;
                    Mark();
                }
            }
            EndUpdate();
        }

        private void Mark()
        {
            if (_batch > 0)
                _dirty = true;
            else
                NotifyChanged();
        }
    }

    /// <summary>One icon of the buff row: a status of the own snapshot with what the HUD needs to draw it.</summary>
    public struct BuffView
    {
        public ushort NetId;
        public string StatusId;

        /// <summary><c>BuffIconRegistry.iconAddress</c>; empty while a placeholder, then the status name shows.</summary>
        public string IconAddress;

        public StatusEffectKind Kind;
        public int Stacks;

        /// <summary>Server tick the status ends at.</summary>
        public uint EndTick;

        public int SortOrder;

        public bool SameAs(in BuffView other) =>
            NetId == other.NetId && Stacks == other.Stacks && EndTick == other.EndTick && IconAddress == other.IconAddress;
    }

    /// <summary>
    /// The buff row (spec 9.3 <c>buff_row</c>, 3.7): the own statuses of the newest snapshot, sorted by
    /// <c>BuffIconRegistry.sortOrder</c> (then in the order applied), plus the armed, mounted and pet flags (spec 9.5).
    /// Changes only when a status, its stacks or its end tick, or a flag changes. Holds every status the protocol carries
    /// (<see cref="Snapshot.MaxOwnStatuses"/>); the presenter shows at most <c>HudSettings.buffRowMaxIcons</c> (WP-M2-11).
    /// A status the newest snapshot no longer carries is gone (it expired or was removed on the server).
    /// </summary>
    public sealed class BuffRowViewModel : ViewModel
    {
        private BuffView[] _entries = new BuffView[Snapshot.MaxOwnStatuses];
        private BuffView[] _scratch = new BuffView[Snapshot.MaxOwnStatuses];

        public int Count { get; private set; }

        public EntityFlags Flags { get; private set; }

        public ref readonly BuffView this[int index] => ref _entries[index];

        /// <summary>The newest snapshot's tick and when it was applied on the display clock (no change event: time left only).</summary>
        public uint LatestTick { get; private set; }

        public double LatestTickAtMs { get; private set; }

        public double TickMs { get; private set; }

        /// <summary>WP-M2-11: the clock for the time left in the tooltip, set with every snapshot.</summary>
        public void SetClock(uint latestTick, double nowMs, double tickMs)
        {
            LatestTick = latestTick;
            LatestTickAtMs = nowMs;
            TickMs = tickMs;
        }

        /// <summary>Whole seconds (rounded up) until the status at <paramref name="index"/> ends, 0 without a clock.</summary>
        public int SecondsLeft(int index, double nowMs)
        {
            if (TickMs <= 0 || index < 0 || index >= Count)
                return 0;
            double leftMs = unchecked((int)(_entries[index].EndTick - LatestTick)) * TickMs - (nowMs - LatestTickAtMs);
            return leftMs <= 0 ? 0 : (int)Math.Ceiling(leftMs / 1000.0);
        }

        public void Set(ReadOnlySpan<OwnStatus> statuses, uint snapshotTick, EntityFlags flags, HudConfig config)
        {
            int count = 0;
            for (int i = 0; i < statuses.Length && count < _scratch.Length; i++)
            {
                var info = config.Status(statuses[i].NetId);
                if (info == null)
                    continue; // tables differ from the server's
                var entry = new BuffView
                {
                    NetId = statuses[i].NetId,
                    StatusId = info.Def.Id,
                    IconAddress = info.Icon?.IconAddress ?? "",
                    Kind = info.Def.Kind,
                    Stacks = statuses[i].Stacks,
                    EndTick = unchecked(snapshotTick + statuses[i].RemainingTicks),
                    SortOrder = info.SortOrder,
                };
                // Insertion sort by sort order, stable: equal orders keep the order applied. No allocation.
                int at = count;
                while (at > 0 && _scratch[at - 1].SortOrder > entry.SortOrder)
                {
                    _scratch[at] = _scratch[at - 1];
                    at--;
                }
                _scratch[at] = entry;
                count++;
            }

            bool same = count == Count && flags == Flags;
            for (int i = 0; same && i < count; i++)
                same = _scratch[i].SameAs(_entries[i]);
            if (same)
                return;
            (_entries, _scratch) = (_scratch, _entries);
            Count = count;
            Flags = flags;
            NotifyChanged();
        }

        public void Clear()
        {
            if (Count == 0 && Flags == EntityFlags.None)
                return;
            Count = 0;
            Flags = EntityFlags.None;
            NotifyChanged();
        }
    }
}
