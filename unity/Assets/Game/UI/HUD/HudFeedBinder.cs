#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.ViewModels.Hud;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace Game.UI.HUD
{
    /// <summary>
    /// Fills the HUD view models from the <see cref="WorldClientFeed"/> (spec 9.4 data bindings; the "network layer to view
    /// model" step of the spec 9.9 flow). The only HUD class that reads the registry and the predictor; it copies what the
    /// server and the prediction report and decides nothing about the game. Display time is the caller's clock in ms.
    /// </summary>
    public sealed class HudFeedBinder : IDisposable
    {
        private static readonly string[] ReasonNames = BuildReasonNames();

        private readonly HudConfig _config;
        private readonly HudViewModels _models;
        private readonly Func<double> _now;
        private WorldClientFeed? _feed;
        private bool _disconnectNotice;

        public HudFeedBinder(HudConfig config, HudViewModels models, Func<double> nowMs)
        {
            _config = config;
            _models = models;
            _now = nowMs;
        }

        public WorldClientFeed? Feed => _feed;

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.SnapshotApplied += OnSnapshot;
            feed.PredictedTick += OnPredictedTick;
            feed.HitsReceived += OnHits;
            feed.ActionResultReceived += OnActionResult;
            feed.PickupRefused += OnPickupRefused;
            feed.IdentityReceived += OnIdentity;
            feed.ChatReceived += OnChatLine;
            feed.ChatRefused += OnChatRefused;
            _models.Chat.Changed += SyncTextInput;
            feed.Disconnected += OnDisconnected;
            feed.Registry.Spawned += OnSpawned;
            feed.Registry.Despawned += OnDespawned;
            foreach (var entity in feed.Registry.Entities.Values)
                OnSpawned(entity);
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.SnapshotApplied -= OnSnapshot;
            _feed.PredictedTick -= OnPredictedTick;
            _feed.HitsReceived -= OnHits;
            _feed.ActionResultReceived -= OnActionResult;
            _feed.PickupRefused -= OnPickupRefused;
            _feed.IdentityReceived -= OnIdentity;
            _feed.ChatReceived -= OnChatLine;
            _feed.ChatRefused -= OnChatRefused;
            _models.Chat.Changed -= SyncTextInput;
            _feed.TextInputActive = false;
            _feed.Disconnected -= OnDisconnected;
            _feed.Registry.Spawned -= OnSpawned;
            _feed.Registry.Despawned -= OnDespawned;
            _feed = null;
        }

        public void Dispose() => Unbind();

        /// <summary>Once per frame: expiries that depend on time only (toast, boss engagement, damage numbers, telegraphs).</summary>
        public void Frame(double nowMs)
        {
            var settings = _config.Settings;
            _models.Toast.Tick(nowMs);
            _models.DamageNumbers.Expire(nowMs, settings.DamageNumberLifetimeMs);
            if (_feed == null)
                return;
            var boss = _models.BossBar;
            if (boss.Visible)
            {
                bool present = _feed.Registry.Entities.TryGetValue(boss.BossId, out var entity);
                bool dead = present && (entity!.ActionState == ActionState.Dead || entity.HpBucket == 0);
                boss.Tick(nowMs, settings.BossEngageTimeoutMs, present, dead);
            }
            if (_feed.HasRenderTick)
                _models.Telegraphs.Expire(_feed.RenderTick);
            // Backlog G: the server refuses every pickup of a dead player (PickupWhileDead), so the prompt hides meanwhile.
            var registry = _feed.Registry;
            bool alive = registry.HasOwnResources && registry.OwnResources.Hp > 0;
            _models.PickupPrompt.Set(_feed.PickupTarget, alive, _feed.PickupKeyName);
        }

        private void OnSnapshot()
        {
            if (_disconnectNotice)
            {
                _disconnectNotice = false;
                _models.Toast.Hide();
            }
            var registry = _feed!.Registry;
            if (registry.HasOwnResources)
                _models.Resources.Set(registry.OwnResources);
            _models.BuffRow.Set(registry.OwnStatuses, registry.OwnStatusesTick, registry.OwnFlags, _config);

            uint tick = registry.OwnStatusesTick; // the newest snapshot's tick
            var plates = _models.NamePlates;
            foreach (var entity in registry.EntityValues)
            {
                // An entity first seen without its spawn event gets its kind from a later spawn, which raises no event.
                if (!plates.TryGetIndex(entity.Id, out _) && entity.Kind != EntityKind.None && plates.ActiveCount < plates.Capacity)
                    OnSpawned(entity);
                plates.SetState(entity.Id, entity.HpBucket, entity.Flags);
                if (entity.Kind == EntityKind.Monster)
                    TrackAttack(entity, tick);
            }
            var boss = _models.BossBar;
            if (boss.Visible && registry.Entities.TryGetValue(boss.BossId, out var bossEntity))
                boss.SetHp(bossEntity.HpBucket);
        }

        /// <summary>Spec 9.3 telegraph: a monster attack with ground shapes, from its start to the end of its Active phase.</summary>
        private void TrackAttack(RemoteEntity monster, uint snapshotTick)
        {
            var telegraphs = _models.Telegraphs;
            bool striking = monster.ActionState == ActionState.Startup || monster.ActionState == ActionState.Channel
                            || monster.ActionState == ActionState.Active;
            if (striking && _config.Shapes(monster.ActionNetId).Length > 0
                && _feed!.Catalog.TryGetIndex(monster.ActionNetId, out int index))
            {
                uint end = unchecked(monster.ActionStartTick + (uint)_feed.Catalog[index].ActiveEnd);
                telegraphs.Upsert(monster.Id, monster.ActionNetId, monster.ActionStartTick, end, monster.TelegraphColor);
            }
            else if (monster.ActionState != ActionState.Recovery) // in Recovery it ran out on its own; otherwise it was cut short
            {
                telegraphs.EndAt(monster.Id, snapshotTick);
            }
        }

        private void OnPredictedTick()
        {
            var feed = _feed!;
            double now = _now();
            double tickMs = feed.TickMs;
            var actions = feed.Predictor.Actions;
            var catalog = feed.Catalog;
            var loadout = actions.Loadout;

            var hotbar = _models.Hotbar;
            hotbar.BeginUpdate();
            hotbar.SetClass(loadout.ClassId);
            for (int slot = 1; slot <= ActionBindings.MaxSkillSlots; slot++)
            {
                int index = loadout.SlotIndex(slot);
                if (index < 0)
                {
                    hotbar.SetAction(slot, "");
                    continue;
                }
                var action = catalog[index];
                hotbar.SetAction(slot, action.Id);
                hotbar.SetCooldown(slot, now, actions.CooldownTicks(index) * tickMs, action.CooldownTicks * tickMs, tickMs);
            }
            hotbar.EndUpdate();

            uint machineTick = actions.Tick;
            _models.Charges.Set(Charge(actions.ParryCharges, machineTick, now, tickMs), Charge(actions.DodgeCharges, machineTick, now, tickMs), tickMs);

            var running = actions.Action;
            if (actions.State == ActionState.Channel && running != null)
            {
                double start = now - (actions.Elapsed - running.StartupEnd) * tickMs;
                double end = now + (running.ChannelEnd - actions.Elapsed) * tickMs;
                _models.CastBar.Show(running.Id, start, end, tickMs);
            }
            else
            {
                _models.CastBar.Hide();
            }
        }

        private static ChargeView Charge(ChargeMeter meter, uint tick, double nowMs, double tickMs)
        {
            int next = meter.TicksUntilNextCharge(tick);
            return new ChargeView(meter.ChargesAt(tick), meter.Max, next > 0 ? nowMs + next * tickMs : 0, meter.RechargeTicks * tickMs);
        }

        private void OnHits(uint serverTick, ReadOnlySpan<HitEntry> hits)
        {
            var registry = _feed!.Registry;
            uint own = registry.OwnEntityId ?? 0;
            double now = _now();
            for (int i = 0; i < hits.Length; i++)
            {
                var hit = hits[i];
                if (!DamageNumberPool.Shows(hit, own))
                    continue;
                AddNumber(hit, now);
                uint other = hit.AttackerId == own ? hit.TargetId : hit.AttackerId;
                if (registry.Entities.TryGetValue(other, out var entity) && entity.Kind == EntityKind.Monster)
                {
                    var def = _config.Monster(entity.DefId);
                    if (def != null && def.Boss)
                        _models.BossBar.Engage(entity.Id, def.Id, def.Rank, entity.HpBucket, now);
                }
            }
        }

        private void AddNumber(in HitEntry hit, double now)
        {
            var text = _config.Text;
            switch (hit.Kind)
            {
                case HitKind.Damaged:
                case HitKind.Killed:
                    _models.DamageNumbers.Add(hit.TargetId, NumberText.Of(hit.Amount), hit.Crit ? DamageNumberStyle.Crit : DamageNumberStyle.Normal, now);
                    break;
                case HitKind.Healed:
                    _models.DamageNumbers.Add(hit.TargetId, text.Get(HudTextIds.HitHeal, new TextArg("amount", NumberText.Of(hit.Amount))),
                        DamageNumberStyle.Heal, now);
                    break;
                case HitKind.Dodged:
                    _models.DamageNumbers.Add(hit.TargetId, text.Get(HudTextIds.HitDodged), DamageNumberStyle.Word, now);
                    break;
                case HitKind.Parried:
                    _models.DamageNumbers.Add(hit.TargetId, hit.Amount > 0 ? NumberText.Of(hit.Amount) : text.Get(HudTextIds.HitParried),
                        hit.Amount > 0 ? DamageNumberStyle.Normal : DamageNumberStyle.Word, now);
                    break;
            }
        }

        private void OnActionResult(ushort actionNetId, byte skillSlot, ActionReason reason)
        {
            double now = _now();
            bool parry = ActionResult.IsParryResult(reason);
            _models.Toast.Show(ReasonName(reason), parry, now, _config.Settings.ToastMs);
            if (!parry && skillSlot > 0)
                _models.Hotbar.Flash(skillSlot, now, _config.Settings.SlotFlashMs);
        }

        /// <summary>A refused pickup shows its reason text in the same toast as refused actions (backlog G).</summary>
        private void OnPickupRefused(PickupReason reason) =>
            _models.Toast.Show(reason.ToString(), false, _now(), _config.Settings.ToastMs);

        private void OnIdentity(uint entityId)
        {
            if (_feed!.Identities.TryGet(entityId, out var name))
                _models.NamePlates.SetName(entityId, name);
        }

        private void OnSpawned(RemoteEntity entity)
        {
            if (entity.Kind == EntityKind.Player)
            {
                _feed!.Identities.TryGet(entity.Id, out var name);
                _models.NamePlates.Add(entity.Id, PlateKind.Player, name, entity.HpBucket, entity.Flags);
            }
            else if (entity.Kind == EntityKind.Monster)
            {
                var def = _config.Monster(entity.DefId);
                string name = def != null ? _config.Text.Get(UiStringCoverage.MonsterId(def.Id)) : "";
                _models.NamePlates.Add(entity.Id, def != null && def.Boss ? PlateKind.Boss : PlateKind.Monster, name, entity.HpBucket, entity.Flags);
            }
        }

        private void OnDespawned(RemoteEntity entity)
        {
            _models.NamePlates.Remove(entity.Id);
            _models.Telegraphs.Remove(entity.Id);
            if (_models.BossBar.Visible && _models.BossBar.BossId == entity.Id)
                _models.BossBar.Hide();
        }

        private void OnChatLine(uint senderEntityId, string senderName, string text) =>
            _models.Chat.AddLine(senderEntityId, senderName, text);

        private void OnChatRefused(ChatReason reason) => _models.Chat.AddRefusal(reason.ToString());

        /// <summary>The world client suspends the gameplay keys while the chat input is open (spec 9.12), from the same frame.</summary>
        private void SyncTextInput()
        {
            if (_feed != null)
                _feed.TextInputActive = _models.Chat.InputOpen;
        }

        /// <summary>
        /// Enter in the chat input (spec 9.12): checks the draft (<see cref="ChatViewModel.Submit"/>) and sends it through the
        /// feed; a line that cannot be sent (no connection) shows <c>ConnectionLost</c>.
        /// </summary>
        public void SubmitChat(string draft)
        {
            var line = _models.Chat.Submit(draft);
            if (line != null && (_feed == null || !_feed.TrySendChat(line)))
                _models.Chat.AddRefusal(nameof(ClientReasonCode.ConnectionLost));
        }

        /// <summary>
        /// Clears the stale HUD and shows why the connection ended (spec 9.10: a kick's code such as <c>Kicked</c>, else for
        /// example <c>ConnectionLost</c>) in the toast, until the world runs again (the next snapshot).
        /// </summary>
        private void OnDisconnected(string reasonCode)
        {
            _models.Clear();
            _disconnectNotice = reasonCode.Length > 0;
            if (_disconnectNotice)
                _models.Toast.Show(reasonCode, false, _now(), double.PositiveInfinity);
        }

        /// <summary>The <c>ReasonCodeStrings</c> code of a reason (its enum member name), without allocating per call.</summary>
        public static string ReasonName(ActionReason reason) =>
            (int)reason < ReasonNames.Length ? ReasonNames[(int)reason] : reason.ToString();

        private static string[] BuildReasonNames()
        {
            var names = new string[(int)ActionResult.LastReason + 1];
            for (int i = 0; i < names.Length; i++)
                names[i] = ((ActionReason)i).ToString();
            return names;
        }
    }
}
