#nullable enable
using System.Linq;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Hud;
using NUnit.Framework;
using Shared.Protocol;

namespace Game.UI.Tests
{
    /// <summary>
    /// Party loot rolls on the HUD (WP-M4-6, spec 4.3 R-4.3-02, 9.12): each roll part becomes a line on the Party tab only, with the
    /// units by mail when the bag was full, marks that tab unread when another is open, and the own winning shows its toast; the
    /// texts are the committed <c>UiStrings</c> rows.
    /// </summary>
    public sealed class HudPartyLootTests
    {
        private const uint Own = 1;

        private static (WorldClientFeed Feed, ChatViewModel Chat, ToastViewModel Toast, HudConfig Hud, InventoryConfig Items) Setup()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            var hud = HudConfig.From(set);
            var items = InventoryConfig.From(set);
            var feed = HudChatTests.Feed();
            OwnSnapshot(feed);
            var chat = new ChatViewModel(20, 120);
            var toast = new ToastViewModel();
            var binder = new PartyLootBinder(chat, toast, items, hud.Text, 1_000, () => 0);
            binder.Bind(feed);
            return (feed, chat, toast, hud, items);
        }

        /// <summary>The own player in the registry from a snapshot (the first entity is the own one).</summary>
        private static void OwnSnapshot(WorldClientFeed feed)
        {
            var buffer = new byte[256];
            var writer = new SnapshotWriter(buffer, new SnapshotHeader { ServerTick = 10 });
            Assert.That(writer.TryAddSpawn(new SpawnEvent { EntityId = Own, Kind = EntityKind.Player, DefId = 1 }), Is.True);
            Assert.That(writer.TryAddEntity(new EntitySnapshot { EntityId = Own, HpBucket = Snapshot.TopHpBucket }), Is.True);
            int size = writer.Finish();
            Assert.That(feed.Registry.ApplySnapshot(new System.ReadOnlySpan<byte>(buffer, 0, size), out _, out _, out _), Is.True);
        }

        private static string HornName(HudConfig hud, InventoryConfig items) => hud.Text.Get(items.Item(1)!.Def.NameId);

        [Test]
        public void A_roll_part_is_a_party_tab_line_and_the_own_winning_a_toast()
        {
            var (feed, chat, toast, hud, items) = Setup();
            string horn = HornName(hud, items);

            feed.RaisePartyLootRolled(new LootRollNotice { DropEntityId = 9, ItemNetId = 1, WinnerEntityId = 2, WinnerName = "Bo", Units = 2 });

            var line = chat.Lines.Single();
            Assert.That(line.Kind, Is.EqualTo(ChatLineKind.Loot));
            Assert.That(line.Text, Is.EqualTo(hud.Text.Get(HudTextIds.PartyLootRoll, new TextArg("name", "Bo"), new TextArg("count", "2"),
                new TextArg("item", horn))));
            Assert.That(line.ShowsOn(ChatChannel.Party), Is.True);
            Assert.That(line.ShowsOn(ChatChannel.Local), Is.False);
            Assert.That(chat.HasUnread(ChatChannel.Party), Is.True); // the Local tab is open
            Assert.That(toast.Visible, Is.False); // somebody else won

            feed.RaisePartyLootRolled(new LootRollNotice { DropEntityId = 9, ItemNetId = 1, WinnerEntityId = Own, WinnerName = "Me", Units = 1, Mailed = 2 });

            Assert.That(chat.Lines.Last().Text, Is.EqualTo(hud.Text.Get(HudTextIds.PartyLootRollMail, new TextArg("name", "Me"),
                new TextArg("count", "3"), new TextArg("item", horn), new TextArg("mailed", "2"))));
            Assert.That(toast.Visible, Is.True);
            Assert.That((toast.TextId, toast.ArgValue, toast.Arg2Value), Is.EqualTo((HudTextIds.ToastLootWonMail, "3", horn)));
        }

        [Test]
        public void The_roll_texts_are_committed_rows()
        {
            var (_, _, _, hud, _) = Setup();
            foreach (string id in new[] { HudTextIds.PartyLootRoll, HudTextIds.PartyLootRollMail, HudTextIds.ToastLootWon, HudTextIds.ToastLootWonMail })
                Assert.That(hud.Text.Has(id), Is.True, id);
        }
    }
}
