using BotClient.World;
using Shared.Protocol;
using Shared.Simulation.Stats;

namespace GameServer.Tests.Bots;

/// <summary>WP-M2-3: the bots' own level and exp come from the reliable <see cref="ProgressionState"/> (protocol 18).</summary>
public class OwnProgressReaderTests
{
    [Fact]
    public void A_progression_state_sets_the_own_level_and_exp_and_other_messages_do_not()
    {
        IBotOwnReader reader = new OwnProgressReader();
        var own = new BotOwnState();
        var gold = ReliableMessages.Write(ReliableMessageId.GoldBalance, new GoldBalance { Gold = 7 });
        reader.OnReliable(ReliableMessageId.GoldBalance, gold, own);
        Assert.Null(own.Level);

        var state = ReliableMessages.Write(ReliableMessageId.ProgressionState, new ProgressionState
        {
            Level = 4, Exp = 51, ExpToNext = 520, Rank = Rank.E, SkillPoints = 4, BandPercent = 100,
            Cause = ProgressionCause.LevelUp,
        });
        reader.OnReliable(ReliableMessageId.ProgressionState, state, own);

        Assert.Equal(4, own.Level);
        Assert.Equal(51, own.Exp);
    }
}
