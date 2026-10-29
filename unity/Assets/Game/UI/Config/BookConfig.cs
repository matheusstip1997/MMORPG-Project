#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;

namespace Game.UI.Config
{
    /// <summary>
    /// <c>UiStrings</c> ids of the Bystial book window (spec 9.1: no user-facing text in code; spec 14.2, 9.13; WP-M2-8). An
    /// EditMode test checks that each constant has a row; the tier, stat, effect, element and family names are
    /// <c>UiStringCoverage</c>'s <c>book.*</c> ids.
    /// </summary>
    public static class BookTextIds
    {
        public const string Title = "book.title";
        public const string Close = "book.close";
        /// <summary>The close button's label for screen readers (spec 9.8).</summary>
        public const string CloseLabel = "book.closeLabel";
        public const string Slots = "book.slots";
        public const string SlotEmpty = "book.slotEmpty";
        public const string Collection = "book.collection";
        public const string Empty = "book.empty";
        /// <summary>A collection row: {name} ({tier}).</summary>
        public const string Card = "book.card";
        public const string NotEarned = "book.notEarned";
        public const string Counter = "book.counter";
        public const string CounterTop = "book.counterTop";
        public const string Placed = "book.placed";
        public const string Hint = "book.hint";
        public const string TakeOut = "book.takeOut";
        /// <summary>A stat bonus: {stat} {value}.</summary>
        public const string Bonus = "book.bonus";
        public const string ToastEarned = "book.toast.earned";
        public const string ToastEvolved = "book.toast.evolved";

        public static readonly string[] All =
        {
            Title, Close, CloseLabel, Slots, SlotEmpty, Collection, Empty, Card, NotEarned, Counter, CounterTop, Placed, Hint,
            TakeOut, Bonus, ToastEarned, ToastEvolved,
        };
    }

    /// <summary>One monster type's card as the window shows it: its name, family, art and tiers (spec 14.2).</summary>
    public sealed class BookCardInfo
    {
        public BookCardInfo(MonsterDef monster, BystialCardArt? art)
        {
            Monster = monster;
            Art = art;
        }

        public MonsterDef Monster { get; }

        public ushort NetId => (ushort)Monster.NetId;

        /// <summary>The card's name is its monster's (<c>monster.&lt;id&gt;</c>).</summary>
        public string NameId => UiStringCoverage.MonsterId(Monster.Id);

        public MonsterFamily Family => Monster.Family;

        /// <summary>The placeholder art (<see cref="BystialCardArt"/>); null without a row.</summary>
        public BystialCardArt? Art { get; }

        public List<BystialCardDef> Tiers { get; } = new List<BystialCardDef>();

        /// <summary>The row of <paramref name="tier"/>; null for none or a tier the ladder lacks.</summary>
        public BystialCardDef? TierRow(BystialTier tier)
        {
            foreach (var row in Tiers)
            {
                if (row.Tier == tier)
                    return row;
            }
            return null;
        }
    }

    /// <summary>
    /// The config tables the Bystial book window reads (spec 14.2, 9.13; WP-M2-8), from a loaded and validated
    /// <see cref="ConfigSet"/>: every carded monster by net id with its tiers and placeholder art, and the toggle key
    /// (<c>Hotkeys</c> row <c>toggleBook</c>). The window only describes cards; the server decides them.
    /// </summary>
    public sealed class BookConfig
    {
        private readonly Dictionary<ushort, BookCardInfo> _cards = new Dictionary<ushort, BookCardInfo>();

        public BookConfig(string toggleBinding)
        {
            ToggleBinding = toggleBinding;
        }

        /// <summary>The <c>toggleBook</c> control path; empty when the table has no row.</summary>
        public string ToggleBinding { get; }

        public IReadOnlyDictionary<ushort, BookCardInfo> Cards => _cards;

        /// <summary>The card of a monster net id; null for one the tables do not card.</summary>
        public BookCardInfo? Card(ushort monsterNetId) => _cards.TryGetValue(monsterNetId, out var card) ? card : null;

        public static BookConfig From(ConfigSet set)
        {
            var config = new BookConfig(Hotkeys.Find(set, Hotkeys.ToggleBook)?.Binding ?? string.Empty);
            if (!set.TryGet(typeof(BystialCardDef), out var cards))
                return config;
            var art = new Dictionary<string, BystialCardArt>(StringComparer.Ordinal);
            if (set.TryGet(typeof(BystialCardArt), out var arts))
            {
                foreach (var row in arts.Rows)
                    art[((BystialCardArt)row).Id] = (BystialCardArt)row;
            }
            foreach (var row in cards.Rows)
            {
                var tier = (BystialCardDef)row;
                var monster = MonsterDef.Find(set, tier.MonsterId);
                if (monster == null)
                    continue;
                if (!config._cards.TryGetValue((ushort)monster.NetId, out var card))
                {
                    card = new BookCardInfo(monster, art.TryGetValue(monster.Id, out var drawn) ? drawn : null);
                    config._cards.Add(card.NetId, card);
                }
                card.Tiers.Add(tier);
            }
            foreach (var card in config._cards.Values)
                card.Tiers.Sort((a, b) => a.Tier.CompareTo(b.Tier));
            return config;
        }
    }
}
