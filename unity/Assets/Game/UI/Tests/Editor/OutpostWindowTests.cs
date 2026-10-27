#nullable enable
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.Menus.Outpost;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Outpost;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace Game.UI.Tests
{
    /// <summary>
    /// The outpost and vendor windows (spec 4.2, 9.13 M2; WP-M2-4): the sale window's sellable stacks, selection, count and
    /// quote states; the vendor window's stock, count and total; the binder's requests (interact, quote, sell with the quoted
    /// total, requote after QuoteExpired, buy), toasts, gold and prompt; and the committed texts.
    /// </summary>
    public sealed class OutpostWindowTests
    {
        private const ushort Horn = 1, Ash = 2, Gland = 4, Potion = 101;
        private const uint OutpostNpc = 7, VendorNpc = 8;

        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        private static OutpostConfig Config(ConfigSet set) => OutpostConfig.From(set, InventoryConfig.From(set));

        private static InventoryContents Contents(params (byte Slot, ushort Item, ushort Count, bool Stolen)[] slots) => new InventoryContents
        {
            Revision = 1,
            SlotCount = 30,
            Slots = slots.Select(s => new InventorySlotEntry { Slot = s.Slot, ItemNetId = s.Item, Count = s.Count, Stolen = s.Stolen }).ToArray(),
        };

        // ---- sale window

        [Test]
        public void The_sale_window_lists_only_sell_only_stacks_that_are_not_stolen()
        {
            var config = Config(Tables());
            var model = new OutpostWindowViewModel();
            model.OpenFor(OutpostNpc);

            model.SetContents(Contents((3, Horn, 20, false), (0, Ash, 5, false), (1, Gland, 2, false), (2, Horn, 4, true)), config.IsSellable);

            Assert.That(model.Stacks.Select(s => (s.Slot, s.ItemNetId, s.Count)), Is.EqualTo(new[] { ((byte)1, Gland, 2), ((byte)3, Horn, 20) }));
        }

        [Test]
        public void Selecting_a_stack_takes_all_its_units_and_the_count_stays_inside_the_stack()
        {
            var config = Config(Tables());
            var model = new OutpostWindowViewModel();
            model.OpenFor(OutpostNpc);
            model.SetContents(Contents((3, Horn, 20, false)), config.IsSellable);

            Assert.That(model.Select(3), Is.True);
            Assert.That((model.SelectedSlot, model.Count, model.Quote), Is.EqualTo((3, 20, QuoteState.None)));
            Assert.That(model.Step(1), Is.False); // already the whole stack
            Assert.That(model.Step(-5), Is.True);
            Assert.That(model.Count, Is.EqualTo(15));
            Assert.That(model.Step(-100), Is.True);
            Assert.That(model.Count, Is.EqualTo(1));
            Assert.That(model.SelectAll(), Is.True);
            Assert.That(model.Count, Is.EqualTo(20));
            Assert.That(model.Select(9), Is.False); // nothing sellable there
        }

        [Test]
        public void Only_the_last_quote_asked_counts_and_a_sale_needs_a_ready_quote()
        {
            var config = Config(Tables());
            var model = new OutpostWindowViewModel();
            model.OpenFor(OutpostNpc);
            model.SetContents(Contents((3, Horn, 20, false)), config.IsSellable);
            model.Select(3);
            Assert.That(model.Confirm(), Is.Null);

            model.QuoteSent(1);
            model.Step(-1);
            model.QuoteSent(2);
            Assert.That(model.OnQuote(new SellQuoteResult { RequestId = 1, Slot = 3, Count = 20, Total = 190 }), Is.Null);
            Assert.That(model.Quote, Is.EqualTo(QuoteState.Pending)); // a stale answer
            model.OnQuote(new SellQuoteResult { RequestId = 2, Slot = 3, Count = 19, Total = 181, FirstUnit = 10, LastUnit = 9 });

            var intent = model.Confirm();
            Assert.That(intent.HasValue, Is.True);
            Assert.That((intent!.Value.Slot, intent.Value.Count, intent.Value.QuotedTotal), Is.EqualTo(((byte)3, (ushort)19, 181L)));
        }

        [Test]
        public void A_sale_shows_its_result_and_a_refusal_returns_its_reason()
        {
            var config = Config(Tables());
            var model = new OutpostWindowViewModel();
            model.OpenFor(OutpostNpc);
            model.SetContents(Contents((3, Horn, 20, false)), config.IsSellable);
            model.Select(3);
            model.QuoteSent(1);
            model.OnQuote(new SellQuoteResult { RequestId = 1, Slot = 3, Count = 20, Total = 190 });
            model.SaleSent(2);
            Assert.That(model.Confirm(), Is.Null); // one sale at a time

            Assert.That(model.OnSale(new SellResult { RequestId = 2, Reason = OutpostReason.QuoteExpired }), Is.EqualTo("QuoteExpired"));
            Assert.That(model.Quote, Is.EqualTo(QuoteState.None));
            model.QuoteSent(3);
            model.OnQuote(new SellQuoteResult { RequestId = 3, Slot = 3, Count = 20, Total = 170 });
            model.SaleSent(4);
            Assert.That(model.OnSale(new SellResult { RequestId = 4, Count = 20, Gold = 170 }), Is.Null);
            Assert.That(model.LastSold, Is.EqualTo((20, 170L)));
            Assert.That(model.SelectedSlot, Is.EqualTo(OutpostWindowViewModel.None));
        }

        // ---- vendor window

        [Test]
        public void The_vendor_window_shows_the_vendor_stock_and_totals_at_the_fixed_price()
        {
            var config = Config(Tables());
            var stock = config.Stock("m1.vendor");
            Assert.That(stock.Select(s => (s.ItemNetId, s.PriceGold)), Is.EqualTo(new[] { (Potion, 25), ((ushort)102, 8) }));
            var model = new VendorWindowViewModel();

            model.OpenFor(VendorNpc, stock);
            Assert.That((model.Selected, model.Count, model.Total), Is.EqualTo((0, 1, 25L)));
            model.Step(3);
            Assert.That(model.Total, Is.EqualTo(100));
            model.Step(100);
            Assert.That(model.Count, Is.EqualTo(20)); // one stack at most
            model.Select(1);
            Assert.That((model.Count, model.Total), Is.EqualTo((1, 8L)));

            var intent = model.Confirm()!.Value;
            model.PurchaseSent(5);
            Assert.That(model.OnPurchase(new VendorBuyResult { RequestId = 5, Reason = OutpostReason.NotEnoughGold }), Is.EqualTo("NotEnoughGold"));
            Assert.That((intent.ItemNetId, intent.Count, intent.Total), Is.EqualTo(((ushort)102, (ushort)1, 8L)));
        }

        // ---- binder

        private sealed class FakeRequests : IWorldRequests
        {
            public List<string> Sent { get; } = new List<string>();

            public uint RequestInventoryMove(byte fromSlot, byte toSlot) => 0;

            public uint RequestOutpostInteract(uint npcEntityId, NpcInteraction kind) => Add($"interact {npcEntityId} {kind}");

            public uint RequestSellQuote(uint npcEntityId, byte slot, ushort count) => Add($"quote {npcEntityId} {slot} {count}");

            public uint RequestSell(uint npcEntityId, byte slot, ushort count, long quotedTotal) =>
                Add($"sell {npcEntityId} {slot} {count} {quotedTotal}");

            public uint RequestVendorBuy(uint npcEntityId, ushort itemNetId, ushort count, long quotedTotal) =>
                Add($"buy {npcEntityId} {itemNetId} {count} {quotedTotal}");

            private uint Add(string request)
            {
                Sent.Add(request);
                return (uint)Sent.Count;
            }
        }

        private static WorldClientFeed Feed(ConfigSet set)
        {
            var settings = PredictionSettings.From(set);
            Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out var error), Is.True, error);
            var catalog = ActionCatalog.From(set);
            return new WorldClientFeed(set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                null, null, settings.TickMs);
        }

        private sealed class Rig
        {
            public Rig()
            {
                var set = Tables();
                Config = Config(set);
                Feed = Feed(set);
                Feed.Requests = Requests;
                Binder = new OutpostBinder(Config, Sell, Vendor, Prompt, Gold, Toast, 2_000, () => 0, readInteractKey: false);
                Binder.Bind(Feed);
            }

            public OutpostConfig Config { get; }
            public WorldClientFeed Feed { get; }
            public FakeRequests Requests { get; } = new FakeRequests();
            public OutpostWindowViewModel Sell { get; } = new OutpostWindowViewModel();
            public VendorWindowViewModel Vendor { get; } = new VendorWindowViewModel();
            public NpcPromptViewModel Prompt { get; } = new NpcPromptViewModel();
            public GoldViewModel Gold { get; } = new GoldViewModel();
            public ToastViewModel Toast { get; } = new ToastViewModel();
            public OutpostBinder Binder { get; }
        }

        [Test]
        public void The_interaction_key_opens_the_outpost_and_a_sale_goes_out_at_the_quoted_total()
        {
            var rig = new Rig();
            rig.Feed.RaiseInventory(Contents((3, Horn, 20, false)));
            rig.Feed.SetNpcTarget(OutpostNpc, 1); // m1.outpost
            rig.Binder.Frame(0);
            Assert.That((rig.Prompt.Visible, rig.Prompt.TextId, rig.Prompt.KeyName), Is.EqualTo((true, OutpostTextIds.PromptOutpost, "T")));

            rig.Binder.Interact();
            rig.Feed.RaiseOutpostInteracted(new OutpostInteractResult { RequestId = 1, NpcEntityId = OutpostNpc, Kind = NpcInteraction.Sell });
            Assert.That(rig.Sell.Open, Is.True);
            rig.Binder.Frame(0);
            Assert.That(rig.Prompt.Visible, Is.False); // its window is open

            rig.Binder.SelectStack(3);
            rig.Feed.RaiseSellQuoted(new SellQuoteResult { RequestId = 2, Slot = 3, Count = 20, Total = 190, FirstUnit = 10, LastUnit = 9 });
            rig.Binder.Sell();
            rig.Feed.RaiseSellAnswered(new SellResult { RequestId = 3, Reason = OutpostReason.QuoteExpired });

            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { "interact 7 Sell", "quote 7 3 20", "sell 7 3 20 190", "quote 7 3 20" }));
            Assert.That(rig.Toast.ReasonCode, Is.EqualTo("QuoteExpired"));
        }

        [Test]
        public void A_refused_interaction_toasts_and_leaving_the_reach_closes_the_window()
        {
            var rig = new Rig();
            rig.Feed.SetNpcTarget(VendorNpc, 2); // m1.vendor
            rig.Binder.Interact();
            rig.Feed.RaiseOutpostInteracted(new OutpostInteractResult { RequestId = 1, NpcEntityId = VendorNpc, Kind = NpcInteraction.Buy, Reason = OutpostReason.OutOfRange });
            Assert.That((rig.Vendor.Open, rig.Toast.ReasonCode), Is.EqualTo((false, "OutOfRange")));

            rig.Binder.Interact();
            rig.Feed.RaiseOutpostInteracted(new OutpostInteractResult { RequestId = 2, NpcEntityId = VendorNpc, Kind = NpcInteraction.Buy });
            Assert.That(rig.Vendor.Open, Is.True);
            rig.Binder.Buy();
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo("buy 8 101 1 25"));

            rig.Feed.SetNpcTarget(0, 0);
            rig.Binder.Frame(0);
            Assert.That(rig.Vendor.Open, Is.False);
        }

        [Test]
        public void The_gold_shows_the_committed_balance_and_clears_on_disconnect()
        {
            var rig = new Rig();
            Assert.That(rig.Gold.Gold, Is.Null);

            rig.Feed.RaiseGold(125);
            Assert.That(rig.Gold.Gold, Is.EqualTo(125));
            rig.Feed.RaiseDisconnected("Disconnected");
            Assert.That(rig.Gold.Gold, Is.Null);
        }

        // ---- committed texts

        [Test]
        public void Committed_ui_strings_cover_every_outpost_text_id_and_reason_and_the_interact_key_is_bound()
        {
            var set = Tables();
            var ui = new HashSet<string>(set.TryGet(typeof(UiStrings), out var table) ? table.Rows.Cast<UiStrings>().Select(r => r.Id) : new string[0]);
            var reasons = new HashSet<string>(set.TryGet(typeof(ReasonCodeStrings), out var codes) ? codes.Rows.Cast<ReasonCodeStrings>().Select(r => r.Code) : new string[0]);

            Assert.That(OutpostTextIds.All.Where(id => !ui.Contains(id)), Is.Empty);
            Assert.That(System.Enum.GetNames(typeof(OutpostReason)).Where(n => n != "None" && !reasons.Contains(n)), Is.Empty);
            Assert.That(Config(set).InteractBinding, Is.EqualTo("<Keyboard>/t"));
            Assert.That(OutpostBinder.KeyNameOf("<Keyboard>/t"), Is.EqualTo("T"));
        }
    }
}
