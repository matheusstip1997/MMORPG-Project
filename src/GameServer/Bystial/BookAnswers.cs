using Shared.Protocol;

namespace GameServer.Bystial;

/// <summary>An accepted book change waiting for the save that holds it (rule 5: commit before confirming).</summary>
public readonly record struct PendingBookConfirmation(string OpKey, BookChangeResult Result);

/// <summary>
/// One player's Bystial book traffic (spec 14.2, WP-M2-8): whether its client needs a <see cref="BookState"/> and why,
/// refusals to send at once, and accepted changes to confirm once committed. World thread only.
/// </summary>
public sealed class BookAnswers
{
    /// <summary>Send a <see cref="BookState"/> in phase 10: the book or a counter changed, or the client is new.</summary>
    public bool Dirty { get; set; }

    /// <summary>The highest cause since the last send.</summary>
    public BookCause Cause { get; set; }

    /// <summary>States sent to this connection (the message's version).</summary>
    public uint Version { get; set; }

    internal List<BookChangeResult> Refusals { get; } = new();

    internal List<PendingBookConfirmation> PendingConfirmations { get; } = new();

    /// <summary>Raises the cause of the next state (an earned card outranks a counter).</summary>
    public void Mark(BookCause cause)
    {
        Dirty = true;
        if (cause > Cause)
            Cause = cause;
    }

    /// <summary>A new connection: the whole book again, its request ids start again.</summary>
    public void Reset()
    {
        ClearAnswers();
        Dirty = true;
        Cause = BookCause.Initial;
        Version = 0;
    }

    public void ClearAnswers()
    {
        Refusals.Clear();
        PendingConfirmations.Clear();
    }
}
