using System.Globalization;
using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>One side of a trade entry (WP-M4-12): an item and count, an amount of gold, or nothing.</summary>
public readonly record struct TradeSide(string ItemId, int Count, long Gold)
{
    public bool IsNothing => ItemId.Length == 0 && Gold == 0;

    public bool IsGold => Gold > 0;

    public override string ToString() =>
        IsNothing ? TradeStep.Nothing : IsGold ? TradeStep.GoldPrefix + Gold.ToString(CultureInfo.InvariantCulture)
        : Count == 1 ? ItemId : ItemId + "x" + Count.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The trade window with the partner (spec 4.5 R-4.5-01, M4 decision 5): <c>trade:&lt;give&gt;:&lt;for&gt;</c>, each side
/// <c>&lt;ItemDef id&gt;[x&lt;count&gt;]</c>, <c>gold=&lt;n&gt;</c> or <c>nothing</c> (<c>trade:m1.demonAsh:gold=10</c> on one bot,
/// <c>trade:gold=10:m1.demonAsh</c> on its partner). The real step: both sides meet at the point <c>trade</c>, the first of
/// the pair invites, each offers its <c>give</c>, checks that the partner's offer is its <c>for</c>, locks and confirms;
/// done on the committed <c>TradeResult</c> with the bag and gold changed (one two-character transaction).
/// <para>
/// WP-M4-12 placeholder (made real by WP-M4-4): the argument is read now (a malformed one throws at the run's start, as
/// <c>level</c> does), the step is "not yet" (no trade protocol yet), logged once per run; the loop and a script go on. The
/// WP replaces <see cref="Tick"/> (contract: Loop/Steps/README.md); an item the tables do not hold is "not yet" with the
/// reason, never an error.
/// </para>
/// </summary>
public sealed class TradeStep : IBotStep
{
    public const string GoldPrefix = "gold=";
    public const string Nothing = "nothing";

    public TradeStep(string argument = "")
    {
        if (!TryParse(argument, out var give, out var wanted))
            throw new ArgumentException($"trade step: '{argument}' is not '<give>:<for>' (each '<item>[x<count>]', 'gold=<n>' or 'nothing')");
        Give = give;
        For = wanted;
    }

    public string Id => "trade";

    /// <summary>What this bot puts into the trade.</summary>
    public TradeSide Give { get; }

    /// <summary>What it expects from the partner.</summary>
    public TradeSide For { get; }

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) =>
        context.NotAvailable(Id, $"no trade protocol yet, WP-M4-4; argument '{Give}:{For}'");

    /// <summary>Reads <c>&lt;give&gt;:&lt;for&gt;</c>; false when either side is malformed or both are nothing.</summary>
    public static bool TryParse(string argument, out TradeSide give, out TradeSide wanted)
    {
        give = wanted = default;
        int colon = (argument ?? "").IndexOf(':');
        if (colon <= 0 || !TryParseSide(argument!.Substring(0, colon), out give) || !TryParseSide(argument.Substring(colon + 1), out wanted))
            return false;
        return !(give.IsNothing && wanted.IsNothing);
    }

    /// <summary>One side: <c>&lt;item&gt;[x&lt;count&gt;]</c> (count 1 to 9999), <c>gold=&lt;n&gt;</c> (n at least 1) or <c>nothing</c>.</summary>
    public static bool TryParseSide(string text, out TradeSide side)
    {
        side = new TradeSide("", 0, 0);
        text = text.Trim();
        if (text == Nothing)
            return true;
        if (text.StartsWith(GoldPrefix, StringComparison.Ordinal))
        {
            if (!long.TryParse(text.AsSpan(GoldPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out long gold) || gold < 1)
                return false;
            side = new TradeSide("", 0, gold);
            return true;
        }
        if (text.Length == 0 || text.Contains(':') || text.Contains('='))
            return false;
        int count = 1;
        int x = text.LastIndexOf('x');
        if (x > 0 && x < text.Length - 1 && text.AsSpan(x + 1).IndexOfAnyExcept("0123456789") < 0)
        {
            if (!int.TryParse(text.AsSpan(x + 1), NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 1 || count > 9_999)
                return false;
            text = text.Substring(0, x);
        }
        side = new TradeSide(text, count, 0);
        return true;
    }
}
