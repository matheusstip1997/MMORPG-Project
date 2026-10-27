using BotClient.Brain;
using Shared.Protocol;

namespace BotClient.Loop;

/// <summary>What a loop step says after a tick.</summary>
public enum BotStepStatus : byte
{
    /// <summary>Still working: the command it filled is sent this tick.</summary>
    Running,
    /// <summary>Finished; the loop starts the next step in the same tick.</summary>
    Done,
    /// <summary>
    /// The game cannot do this yet (its protocol has not landed): logged once per run through
    /// <see cref="BotStepContext.NotAvailable"/>, counted, and the loop goes on as if it were done.
    /// </summary>
    NotAvailable,
    /// <summary>It tried and could not (for example a request the server refused); counted, and the loop goes on.</summary>
    Failed,
}

/// <summary>
/// One step of the M2 loop scenario (WP-M2-12, spec 11.5): fight, pickup, sell, buy, run, learn, rankUp, equip
/// (<see cref="Shared.Data.Tables.BotBehaviour.LoopStepIds"/>). <see cref="BotLoop"/> plays the behaviour's
/// <c>loopSteps</c> in order, round after round; every step lives in its own file under <c>Loop/Steps</c>, so a WP adds
/// its step by filling in that one file.
/// <para>
/// The contract: a step reads the bot's world only through <see cref="BotStepContext"/> (the Unity client's engine-free
/// state from ClientCore: entity registry, inventory, own state, predicted position; never server code), acts only by
/// filling the tick's <see cref="BotCommand"/> (keys, facing, buttons) or by sending a reliable request with
/// <see cref="BotStepContext.SendReliable"/>, and reports progress with <see cref="BotStepContext.Count"/>. A step that
/// returns anything but <see cref="BotStepStatus.Running"/> leaves the command alone (the next step fills it). It runs on
/// the bots' simulation thread, one call at a time.
/// </para>
/// </summary>
public interface IBotStep
{
    /// <summary>Its id in <c>BotBehaviour.loopSteps</c>.</summary>
    string Id { get; }

    /// <summary>The step becomes the loop's current step (once per round).</summary>
    void Begin(BotStepContext context);

    /// <summary>One client tick while it is the current step.</summary>
    BotStepStatus Tick(BotStepContext context, ref BotCommand command);

    /// <summary>
    /// Every reliable message the bot receives, after the world client's own handling, whether or not this step is the
    /// current one (a result can arrive after it moved on). Read MemoryPack messages through <c>ReliableMessages</c>.
    /// </summary>
    void OnReliable(BotStepContext context, ReliableMessageId id, ReadOnlySpan<byte> message)
    {
    }
}
