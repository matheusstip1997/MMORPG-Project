using Game.Gameplay.Entities;
using GameServer.Dev.Commands;

namespace GameServer.Tests.Social;

/// <summary>
/// The contract between the two command tables (spec 9.12, 11.7; WP-M4-2, PR #176 review): the client reads its player
/// commands (<see cref="ChatCommandParser"/>) before anything is sent and passes every other <c>/</c> line to the dev console
/// (<see cref="DevCommandParser"/>), so no word may be in both; a word added to either table must not shadow the other.
/// </summary>
public class PlayerCommandTableTests
{
    [Fact]
    public void No_player_command_word_is_a_dev_console_command()
    {
        foreach (var spec in ChatCommandParser.Commands)
            Assert.Null(DevCommandParser.Find(spec.Word));
    }

    [Fact]
    public void Every_dev_console_line_passes_the_client_parser_untouched()
    {
        foreach (var spec in DevCommandParser.Commands)
            Assert.Equal(ChatCommandParse.NotACommand, ChatCommandParser.TryParse("/" + spec.Name + " x", out _));
    }

    [Fact]
    public void The_player_command_words_are_unique()
    {
        var words = ChatCommandParser.Commands.Select(c => c.Word.ToLowerInvariant()).ToList();
        Assert.Equal(words.Count, words.Distinct().Count());
    }
}
