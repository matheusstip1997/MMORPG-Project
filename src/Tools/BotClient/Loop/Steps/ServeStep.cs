using BotClient.Brain;
using Game.Gameplay.Entities;
using Shared.Data.Tables;
using Shared.Protocol;

namespace BotClient.Loop.Steps;

/// <summary>
/// The QA partner bot's standing step (WP-M4-12, spec 11.5; <c>tools/qa/Run-QaSmoke.ps1 -Partner</c>): the bot holds still
/// and waits for the smoke's requests on the file pair board. A request <c>script.&lt;n&gt;</c> names a partner script
/// <c>&lt;id&gt;</c>: the step plays the <c>BotBehaviour</c> row <c>qa-partner-&lt;id&gt;</c> (a <c>loopOnce</c> script) inside its
/// own entry, with the same context (so its steps read the serving row's tuning, <c>context.Behaviour</c>; the script row
/// gives its entries and timeouts), then answers <c>result.&lt;n&gt;</c> with <see cref="PairScriptResult"/>'s text and holds
/// again. A request it cannot play (no such row, not a script, an unknown step) is answered as failed. Done when the
/// smoke's side posted <c>gone</c>. A script still running when the smoke gives its request up (<c>cancel.&lt;n&gt;</c>, posted
/// after every smoke step that ended without its answer) or posts <c>gone</c> stops at once: its meetings are given up, a party
/// left, a trade cancelled, and the request answered <c>cancelled; &lt;entry&gt;: &lt;why&gt;</c> (QA M4 wave 1 F2).
/// Counters: <c>serve.scripts</c>, <c>serve.unknown</c>, <c>serve.cancelled</c>, <c>serve.partnerGone</c>.
/// <para>A smoke step that needs the partner adds its <c>qa-partner-&lt;step&gt;</c> row (contract: <c>Loop/Steps/README.md</c>).</para>
/// </summary>
public sealed class ServeStep : IBotStep
{
    /// <summary>The prefix of the partner scripts' <c>BotBehaviour</c> ids.</summary>
    public const string ScriptPrefix = "qa-partner-";

    private int _handled;
    private int _number;
    private BotLoop? _script;

    public string Id => "serve";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        var pair = context.Pair;
        if (pair == null)
        {
            context.Count("serve.noPartner");
            return context.Failed("no pair board: serve is the QA partner bot's step (BotClient run --partner-dir)");
        }
        if (_script == null)
        {
            if (pair.PartnerGone)
            {
                context.Count("serve.partnerGone");
                return BotStepStatus.Done;
            }
            if (!pair.TryReadRequest(_handled + 1, out string scriptId))
                return BotStepStatus.Running; // stand by
            _number = ++_handled;
            string behaviourId = ScriptPrefix + scriptId;
            var behaviour = context.Data.Rows<BotBehaviour>().FirstOrDefault(b => b.Id == behaviourId);
            if (behaviour is not { LoopOnce: true })
            {
                context.Count("serve.unknown");
                pair.Answer(_number, PairScriptResult.Failed + "; no script row '" + PairLink.Clean(behaviourId) + "' (a loopOnce BotBehaviour)");
                return BotStepStatus.Running;
            }
            try
            {
                _script = context.NewLoop(behaviour);
            }
            catch (ArgumentException e)
            {
                context.Count("serve.unknown");
                pair.Answer(_number, PairScriptResult.Failed + "; " + PairLink.Clean(e.Message));
                return BotStepStatus.Running;
            }
            context.Log($"serve: request {_number} plays '{behaviourId}'");
        }
        if (pair.IsCancelled(_number) || pair.PartnerGone)
        {
            // QA M4 wave 1 F2: the smoke's step ended before this script answered (any exit path: a failure, a timeout, a
            // throw), or the smoke left. The script stops here, so it never holds the bot for the next request.
            string reason = pair.IsCancelled(_number) ? "the player gave the request up" : "the player left (gone)";
            string entry = _script.Abort(context, reason);
            CleanUp(context);
            string cancelled = Result(_script.Outcomes, PairScriptResult.Cancelled + "; " + entry + ": " + reason);
            pair.Answer(_number, cancelled);
            context.Log($"serve: request {_number} cancelled: {cancelled}");
            context.Count("serve.cancelled");
            _script = null;
            return BotStepStatus.Running; // gone: the next tick ends serve
        }
        var scripted = _script.Tick(context);
        if (!_script.Finished)
        {
            command = scripted;
            return BotStepStatus.Running;
        }
        string result = Result(_script.Outcomes);
        pair.Answer(_number, result);
        context.Log($"serve: request {_number} answered: {result}");
        context.Count("serve.scripts");
        _script = null;
        return BotStepStatus.Running;
    }

    public void OnReliable(BotStepContext context, ReliableMessageId id, ReadOnlySpan<byte> message) =>
        _script?.OnReliable(context, id, message);

    /// <summary>
    /// The answer's text: <c>done</c> when every entry was done, else the first other entry's
    /// <c>&lt;outcome&gt;; &lt;entry&gt;: &lt;reason&gt;</c>; then every entry as <c>; &lt;entry&gt; &lt;outcome&gt;</c>.
    /// </summary>
    public static string Result(IReadOnlyList<StepResult> outcomes, string? head = null)
    {
        var first = outcomes.FirstOrDefault(o => o.Outcome != StepOutcome.Done);
        head ??= first == null ? PairScriptResult.Done : OutcomeWord(first.Outcome) + "; " + first.Entry + ": " + first.Reason;
        string text = head + string.Concat(outcomes.Select(o => "; " + o.Entry + " " + OutcomeWord(o.Outcome)));
        return PairLink.Clean(text);
    }

    /// <summary>
    /// After a cancelled script: what it left open with the player goes (QA M4 wave 1 F2), so the next step starts clean:
    /// the party is left, an open trade cancelled and an invite declined. A stall or a listing stays (its own close and
    /// expiry end it, as a player's would).
    /// </summary>
    private static void CleanUp(BotStepContext context)
    {
        if (context.Party.InParty)
            context.SendReliable(context.Party.Request(PartyAction.Leave, out _));
        if (context.Trade.IsOpen)
            context.SendReliable(context.Trade.Cancel(out _));
        else if (context.Trade.IsInvited)
            context.SendReliable(context.Trade.Decline(out _));
    }

    private static string OutcomeWord(StepOutcome outcome) => outcome switch
    {
        StepOutcome.Done => PairScriptResult.Done,
        StepOutcome.NotYet => PairScriptResult.NotYet,
        StepOutcome.TimedOut => PairScriptResult.TimedOut,
        StepOutcome.Pending => "pending",
        _ => PairScriptResult.Failed,
    };
}
