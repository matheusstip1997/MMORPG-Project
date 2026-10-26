#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Hud;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace Game.UI.Tests
{
    /// <summary>
    /// Spec 9.3 <c>pickup_prompt</c> (backlog G): the HUD shows "Press G to pick up" only while the pickup key would ask for
    /// a drop the server accepts for range (<c>LootSettings.pickupRangeCm</c> of the committed tables, from both the predicted
    /// and the last server-confirmed own position) and the player is alive; the text names the pickup binding's key.
    /// </summary>
    public sealed class HudPickupPromptTests
    {
        private const uint Self = 1, Drop = 2;

        private ConfigSet _set = null!;
        private HudConfig _config = null!;
        private WorldClientFeed _feed = null!;
        private HudViewModels _models = null!;
        private HudFeedBinder _binder = null!;
        private ClientInventory _inventory = null!;
        private int _rangeCm;
        private uint _tick;

        [SetUp]
        public void SetUp()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            _set = set!;
            _config = HudConfig.From(_set);
            Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out error), Is.True, error);
            var settings = PredictionSettings.From(_set);
            var catalog = ActionCatalog.From(_set);
            _feed = new WorldClientFeed(_set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                null, null, settings.TickMs);
            _models = new HudViewModels(4, 4, _config.Chat.HistoryLines, _config.Chat.MaxChars);
            _binder = new HudFeedBinder(_config, _models, () => 0);
            _binder.Bind(_feed);
            _inventory = new ClientInventory(_set);
            _rangeCm = _set.Single<LootSettings>().PickupRangeCm;
            _tick = 10;
        }

        [TearDown]
        public void TearDown() => _binder.Dispose();

        [Test]
        public void The_prompt_shows_at_a_drop_at_the_servers_pickup_range_and_names_the_pickup_key()
        {
            Frame(dropXCm: _rangeCm, predictedXCm: 0, confirmedXCm: 0, hp: 100);

            var prompt = _models.PickupPrompt;
            Assert.That(prompt.Visible, Is.True);
            Assert.That(prompt.DropId, Is.EqualTo(Drop));
            Assert.That(prompt.KeyName, Is.EqualTo("G"));
            Assert.That(_config.Text.Get(HudTextIds.PickupPrompt, new TextArg("key", prompt.KeyName)), Is.EqualTo("Press G to pick up"));
        }

        [Test]
        public void The_prompt_hides_for_a_drop_one_cm_past_the_servers_pickup_range()
        {
            Frame(dropXCm: _rangeCm, predictedXCm: 0, confirmedXCm: 0, hp: 100);
            Assert.That(_models.PickupPrompt.Visible, Is.True);

            Frame(dropXCm: _rangeCm + 1, predictedXCm: 0, confirmedXCm: 0, hp: 100);

            Assert.That(_models.PickupPrompt.Visible, Is.False, "the server refuses this pickup with OutOfRange");
            Assert.That(_models.PickupPrompt.DropId, Is.EqualTo(0u));
        }

        [Test]
        public void Walking_up_the_prompt_waits_until_the_server_has_the_player_in_range()
        {
            // The prediction is in range, the server still has the player 50 cm further away: a press now could be refused.
            Frame(dropXCm: _rangeCm, predictedXCm: 0, confirmedXCm: -50, hp: 100);
            Assert.That(_models.PickupPrompt.Visible, Is.False);

            Frame(dropXCm: _rangeCm, predictedXCm: 0, confirmedXCm: 0, hp: 100);
            Assert.That(_models.PickupPrompt.Visible, Is.True);
        }

        [Test]
        public void The_prompt_hides_while_the_player_is_dead_and_returns_after_the_respawn()
        {
            Frame(dropXCm: 100, predictedXCm: 0, confirmedXCm: 0, hp: 0);
            Assert.That(_models.PickupPrompt.Visible, Is.False, "the server refuses every pickup with PickupWhileDead");

            Frame(dropXCm: 100, predictedXCm: 0, confirmedXCm: 0, hp: 100);
            Assert.That(_models.PickupPrompt.Visible, Is.True);
        }

        [Test]
        public void A_rebind_changes_the_key_the_prompt_names()
        {
            Frame(dropXCm: 100, predictedXCm: 0, confirmedXCm: 0, hp: 100);
            int changes = 0;
            _models.PickupPrompt.Changed += () => changes++;

            _feed.SetPickupTarget(_feed.PickupTarget, "H");
            _binder.Frame(0);

            Assert.That(_models.PickupPrompt.KeyName, Is.EqualTo("H"));
            Assert.That(changes, Is.EqualTo(1));
            _binder.Frame(0);
            Assert.That(changes, Is.EqualTo(1), "the model changes only on a real change");
        }

        [Test]
        public void A_disconnect_hides_the_prompt()
        {
            Frame(dropXCm: 100, predictedXCm: 0, confirmedXCm: 0, hp: 100);

            _feed.RaiseDisconnected("");
            _binder.Frame(0);

            Assert.That(_models.PickupPrompt.Visible, Is.False);
        }

        /// <summary>One snapshot (own entity at x 0, the drop at <paramref name="dropXCm"/>), then the world client's and the HUD's frame.</summary>
        private void Frame(int dropXCm, int predictedXCm, int confirmedXCm, int hp)
        {
            bool first = _tick == 10;
            Assert.That(_feed.Registry.ApplySnapshot(Snapshot(_tick++, dropXCm, hp, first), out _, out _, out bool hasOwn) && hasOwn, Is.True);
            _feed.RaiseSnapshotApplied();
            _feed.SetPickupTarget(_inventory.NearestDrop(_feed.Registry, predictedXCm, 0, confirmedXCm, 0), "G");
            _binder.Frame(0);
        }

        private static byte[] Snapshot(uint tick, int dropXCm, int hp, bool spawn)
        {
            var buffer = new byte[2048];
            var header = new SnapshotHeader
            {
                ServerTick = tick,
                HasOwnResources = true,
                Own = new OwnResources { Hp = hp, MaxHp = 100, Mp = 10, MaxMp = 10, Stamina = 100, MaxStamina = 100 },
            };
            var writer = new SnapshotWriter(buffer, header);
            if (spawn)
            {
                Assert.That(writer.TryAddSpawn(new SpawnEvent { EntityId = Self, Kind = EntityKind.Player, DefId = 1 }), Is.True);
                Assert.That(writer.TryAddSpawn(new SpawnEvent { EntityId = Drop, Kind = EntityKind.LootDrop, DefId = 1 }), Is.True);
            }
            byte bucket = (byte)(hp > 0 ? Shared.Protocol.Snapshot.TopHpBucket : 0);
            Assert.That(writer.TryAddEntity(new EntitySnapshot { EntityId = Self, HpBucket = bucket }), Is.True); // the observer first
            Assert.That(writer.TryAddEntity(new EntitySnapshot { EntityId = Drop, XCm = dropXCm, HpBucket = Shared.Protocol.Snapshot.TopHpBucket }), Is.True);
            return buffer.AsSpan(0, writer.Finish()).ToArray();
        }
    }
}
