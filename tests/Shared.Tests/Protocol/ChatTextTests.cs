using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>The chat line rule shared by the server and the client pre-check (R-6.5-01, spec 9.12).</summary>
public class ChatTextTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("　\t ")]
    public void Nothing_but_white_space_is_empty(string? text)
    {
        Assert.Equal(ChatReason.ChatEmpty, ChatText.Check(text, 10, out var clean));
        Assert.Equal(string.Empty, clean);
    }

    [Fact]
    public void White_space_at_both_ends_is_trimmed()
    {
        Assert.Equal(ChatReason.None, ChatText.Check("  hello there \t", 20, out var clean));
        Assert.Equal("hello there", clean);
    }

    [Fact]
    public void Exactly_max_chars_is_allowed_and_one_more_is_too_long()
    {
        Assert.Equal(ChatReason.None, ChatText.Check(new string('a', 10), 10, out _));
        Assert.Equal(ChatReason.ChatTooLong, ChatText.Check(new string('a', 11), 10, out var clean));
        Assert.Equal(string.Empty, clean);
    }

    [Fact]
    public void Trimmed_white_space_does_not_count()
    {
        Assert.Equal(ChatReason.None, ChatText.Check("   " + new string('a', 10) + "   ", 10, out _));
    }

    [Fact]
    public void A_surrogate_pair_counts_as_one_character()
    {
        // Five emoji are ten UTF-16 units but five characters.
        Assert.Equal(ChatReason.None, ChatText.Check("😀😀😀😀😀", 5, out _));
        Assert.Equal(ChatReason.ChatTooLong, ChatText.Check("😀😀😀😀😀😀", 5, out _));
    }

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("tab\tinside")]
    [InlineData("bell\u0007")]
    [InlineData("c1\u0085control")]
    public void Control_characters_are_invalid(string text)
    {
        Assert.Equal(ChatReason.ChatInvalidText, ChatText.Check(text, 200, out var clean));
        Assert.Equal(string.Empty, clean);
    }

    [Fact]
    public void Lone_surrogates_are_invalid()
    {
        // Built in code: attribute arguments cannot hold lone surrogates (the compiler stores them as U+FFFD).
        foreach (var text in new[] { "lone " + '\uD83D' + " high", "lone " + '\uDE00' + " low", "ends high " + '\uD83D' })
            Assert.Equal(ChatReason.ChatInvalidText, ChatText.Check(text, 200, out _));
    }

    [Fact]
    public void Invalid_text_is_reported_even_when_also_too_long()
    {
        Assert.Equal(ChatReason.ChatInvalidText, ChatText.Check(new string('a', 50) + "\n" + new string('a', 50), 10, out _));
    }

    [Fact]
    public void Markup_is_plain_text_here()
    {
        // The client draws chat with rich text off; the rule itself does not care about angle brackets.
        Assert.Equal(ChatReason.None, ChatText.Check("<color=red>hi</color>", 200, out _));
    }
}
