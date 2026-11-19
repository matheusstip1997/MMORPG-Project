#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.Menus.Auction;
using Game.UI.Menus.Contracts;
using Game.UI.Menus.LootBag;
using Game.UI.Menus.Mail;
using Game.UI.Menus.Social;
using Game.UI.Menus.Stall;
using Game.UI.Menus.Trade;
using Game.UI.Presenters;
using Game.UI.ViewModels.Auction;
using Game.UI.ViewModels.Contracts;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.LootBag;
using Game.UI.ViewModels.Mail;
using Game.UI.ViewModels.Outpost;
using Game.UI.ViewModels.Social;
using Game.UI.ViewModels.Stall;
using Game.UI.ViewModels.Trade;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.Tests
{
    /// <summary>
    /// WP-M4-11 (spec 9.13 window pattern; user answers 2026-11-17): every M4 window has the shared header (its title, a close
    /// button labelled for screen readers), its text fields the one look of D3, one badge rule for M, C and SO, the stolen mark,
    /// dev console lines on the Local tab only, every left-edge window in the one-at-a-time group with a toast when one opens
    /// during a trade, Esc closing the group's window, and the left-edge and open-together layout rules for the M4 rows.
    /// </summary>
    public sealed class WindowChromeTests
    {
        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        private static void AssertChrome(VisualElement root, string closeName, string window)
        {
            var close = root.Q<Button>(closeName);
            Assert.That(close, Is.Not.Null, window + ": no close button " + closeName);
            Assert.That(close!.ClassListContains(WindowChrome.CloseClass), Is.True, window + ": the close is not the shared one");
            Assert.That(close.tooltip, Is.Not.Empty, window + ": the close has no text label (spec 9.8)");
            var header = close.parent;
            Assert.That(header.ClassListContains(WindowChrome.HeaderClass), Is.True, window + ": no shared header");
            Assert.That(header.Q<Label>(className: WindowChrome.TitleClass), Is.Not.Null, window + ": no title");
        }

        private static void AssertFields(VisualElement root, HudTheme theme, string window)
        {
            var fields = root.Query<TextField>().ToList();
            foreach (var field in fields)
            {
                Assert.That(field.ClassListContains(WindowChrome.FieldClass), Is.True, window + ": field " + field.name + " without the shared look (D3)");
                var input = field.Q(className: TextField.inputUssClassName);
                Assert.That(input.style.color.value, Is.EqualTo(theme.Text), window + ": typed text not in the text token");
                Assert.That(input.style.backgroundColor.value, Is.EqualTo(theme.Panel), window + ": the field is not dark");
            }
        }

        [Test]
        public void Every_m4_window_has_the_shared_header_with_a_labelled_close_and_the_shared_field_look()
        {
            var set = Tables();
            var hud = HudConfig.From(set);
            var inventory = InventoryConfig.From(set);
            var toast = new ToastViewModel();
            Func<double> now = () => 0;
            Func<long> utc = () => 0;
            var built = new List<(HudPresenter Presenter, HudView View, string Close, string Name)>();
            void Build(HudPresenter presenter, HudView view, string close, string name)
            {
                presenter.Bind();
                built.Add((presenter, view, close, name));
            }

            var mailView = new HudView(hud);
            var mail = MailConfig.From(set);
            var mailModel = new MailWindowViewModel();
            Build(new MailWindowPresenter(hud, mailView, mail, mailModel,
                new MailWindowBinder(mail, hud.Text, mailModel, toast, 2_000, now, readToggleKey: false), utc), mailView, "mail_close", "mail");

            var auctionView = new HudView(hud);
            var auction = AuctionConfig.From(set, inventory);
            var auctionModel = new AuctionWindowViewModel();
            Build(new AuctionWindowPresenter(hud, auctionView, auction, auctionModel,
                new AuctionBinder(auction, hud.Text, auctionModel, toast, 2_000, now), utc), auctionView, "auction_close", "auction");

            var stallView = new HudView(hud);
            var stall = StallConfig.From(set, inventory);
            var stallModel = new StallWindowViewModel();
            Build(new StallWindowPresenter(hud, stallView, stall, stallModel,
                new StallBinder(stall, hud.Text, stallModel, new NpcPromptViewModel(), toast, 2_000, now, readInteractKey: false), utc),
                stallView, "stall_close", "stall");

            var bagView = new HudView(hud);
            var bagModel = new LootBagWindowViewModel();
            Build(new LootBagWindowPresenter(hud, bagView, LootBagConfig.From(set), bagModel, new LootBagWindowBinder(bagModel, toast, 2_000, now), now),
                bagView, "loot_bag_close", "loot bag");

            var socialView = new HudView(hud);
            var socialModel = new SocialWindowViewModel();
            Build(new SocialWindowPresenter(hud, socialView, socialModel,
                new SocialWindowBinder(SocialConfig.From(set), socialModel, toast, 2_000, now, readToggleKey: false)), socialView, "social_close", "social");

            var tradeView = new HudView(hud);
            var tradeModel = new TradeWindowViewModel();
            Build(new TradeWindowPresenter(hud, tradeView, inventory, tradeModel, new TradeBinder(tradeModel, new TradeInviteViewModel(), toast, 2_000, now)),
                tradeView, "trade_close", "trade");

            var contracts = ContractConfig.From(set, inventory);
            var contractModel = new ContractWindowViewModel(contracts);
            var boardModel = new ContractBoardViewModel();
            var contractBinder = new ContractBinder(contracts, contractModel, boardModel, toast, 2_000, now, readToggleKey: false);
            var contractView = new HudView(hud);
            Build(new ContractWindowPresenter(hud, contractView, contracts, contractModel, contractBinder, utc), contractView, "contract_close", "C window");
            var boardView = new HudView(hud);
            Build(new ContractBoardPresenter(hud, boardView, contracts, boardModel, contractBinder, utc), boardView, "contract_board_close", "contract board");

            foreach (var (presenter, view, close, name) in built)
            {
                AssertChrome(view.Root, close, name);
                AssertFields(view.Root, hud.Theme, name);
                presenter.Dispose();
            }
            Assert.That(tradeView.Root.Q<ScrollView>("trade_own_list"), Is.Not.Null, "the trade offers scroll");
            Assert.That(tradeView.Root.Q<ScrollView>("trade_partner_list"), Is.Not.Null, "the trade offers scroll");
        }

        [Test]
        public void A_badge_spells_its_count_up_to_the_cap_and_is_empty_at_zero()
        {
            int max = HudConfig.From(Tables()).Settings.BadgeMaxCount;
            Assert.That(max, Is.EqualTo(99));
            Assert.That(ShortcutRowPresenter.BadgeLabel(0, max), Is.Empty);
            Assert.That(ShortcutRowPresenter.BadgeLabel(7, max), Is.EqualTo("7"));
            Assert.That(ShortcutRowPresenter.BadgeLabel(99, max), Is.EqualTo("99"));
            Assert.That(ShortcutRowPresenter.BadgeLabel(140, max), Is.EqualTo("99+"));
        }

        [Test]
        public void The_stolen_mark_is_a_shape_with_its_tag_and_label_hidden_until_set()
        {
            var hud = HudConfig.From(Tables());
            var mark = WindowChrome.StolenMark(hud);
            Assert.That(mark.style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(mark.tooltip, Is.EqualTo(hud.Text.Get(InventoryTextIds.TooltipStolen)));
            Assert.That(mark.Q<Label>().text, Is.EqualTo(hud.Text.Get(InventoryTextIds.SlotStolen)));
            Assert.That(mark.childCount, Is.EqualTo(2), "a diamond and its tag: shape and text, not color alone (9.8)");
            WindowChrome.SetStolen(mark, true);
            Assert.That(mark.style.display.value, Is.EqualTo(DisplayStyle.Flex));
        }

        [Test]
        public void Dev_console_lines_show_on_the_local_tab_only_and_refusals_on_every_tab()
        {
            var chat = new ChatViewModel(10, 200);
            chat.AddDevAnswer(DevReason.None, new[] { "you died" });
            chat.AddRefusal(nameof(ChatReason.ChatRateLimited));
            var dev = chat.Lines.First(l => l.Kind == ChatLineKind.System);
            var refusal = chat.Lines.Last();
            Assert.That((dev.ShowsOn(ChatChannel.Local), dev.ShowsOn(ChatChannel.Party), dev.ShowsOn(ChatChannel.Whisper)),
                Is.EqualTo((true, false, false)));
            Assert.That((refusal.ShowsOn(ChatChannel.Local), refusal.ShowsOn(ChatChannel.Party), refusal.ShowsOn(ChatChannel.Whisper)),
                Is.EqualTo((true, true, true)));
        }

        [Test]
        public void A_window_that_opens_during_a_trade_is_closed_and_reported_and_esc_closes_the_open_one()
        {
            bool trade = false, sheet = false, forge = false;
            int forgeCloses = 0;
            var group = new WindowGroup { Blocked = () => trade };
            group.Join(HudElementIds.CharacterSheet, () => sheet, () => sheet = false);
            group.Join(HudElementIds.ForgeWindow, () => forge, () =>
            {
                forge = false;
                forgeCloses++;
            });
            var reported = new List<string>();
            group.OpenedWhileBlocked += reported.Add;

            sheet = true;
            group.Frame();
            forge = true; // the forge opens: the sheet closes (one at a time)
            group.Frame();
            Assert.That((sheet, forge), Is.EqualTo((false, true)));

            trade = true; // a trade closes it through its own close; not reported (it was open before)
            group.Frame();
            Assert.That((forge, forgeCloses, reported.Count), Is.EqualTo((false, 1, 0)));
            sheet = true; // the sheet's key during the trade
            group.Frame();
            Assert.That(sheet, Is.False);
            Assert.That(reported, Is.EqualTo(new[] { HudElementIds.CharacterSheet }));

            trade = false;
            sheet = true;
            group.Frame();
            Assert.That(group.CloseOpen(), Is.True); // Esc
            Assert.That((sheet, group.CloseOpen()), Is.EqualTo((false, false)));
        }

        [Test]
        public void Esc_is_a_placeholder_hotkey_and_the_m4_windows_keep_the_layout_rules()
        {
            var set = Tables();
            Assert.That(Hotkeys.Find(set, Hotkeys.CloseWindow)?.Binding, Is.EqualTo("<Keyboard>/escape"));
            var hud = HudConfig.From(set);
            Rect Box(string id)
            {
                var row = hud.LayoutRows.Single(r => r.Id == id);
                var rect = UiLayout.Resolve(row.Anchor, row.Offset, row.Size, 1280, 720);
                return new Rect(rect.x, rect.y, rect.width, rect.height);
            }

            // The M4 left-edge windows clear the gold line and the chat; those that take a drop clear the inventory window.
            foreach (var id in new[] { HudElementIds.ContractWindow, HudElementIds.ContractBoard, HudElementIds.TradeWindow, HudElementIds.AuctionWindow,
                         HudElementIds.StallWindow, HudElementIds.MailWindow, HudElementIds.SocialWindow, HudElementIds.LootBagWindow })
            {
                Assert.That(Box(id).Overlaps(Box(HudElementIds.Gold)), Is.False, id + " covers the gold line");
                Assert.That(Box(id).Overlaps(Box(HudElementIds.Chat)), Is.False, id + " covers the chat");
                Assert.That(Box(id).Overlaps(Box(HudElementIds.InventoryWindow)), Is.False, id + " covers the inventory window");
            }
        }
    }
}
