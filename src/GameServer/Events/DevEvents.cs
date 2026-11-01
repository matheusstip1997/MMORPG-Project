namespace GameServer.Events;

/// <summary>
/// A dev console command ended (WP-M3-13, spec 11.7 dev console): ran (published once its save committed, rule 5) or refused
/// (at once). For telemetry (<c>DevCommand</c>: who, which command, its main argument and value, the outcome), so every
/// state a dev command made is traceable next to the writers' own audit rows. Only a DevTools build with
/// <c>MMORPG_DEV_TOOLS</c> on publishes it.
/// </summary>
/// <param name="Command">The <c>DevCommandKind</c>.</param>
/// <param name="Subject">The command's main id as a net id (item, monster, job; 0 for none).</param>
/// <param name="Value">Its main number (count, level, amount; 0 for none).</param>
/// <param name="Reason">The <c>DevReason</c>: <c>None</c> for a command that ran.</param>
public readonly record struct DevCommandRan(long CharacterId, long AccountId, uint EntityId, byte Command, int Subject, long Value,
    Shared.Protocol.DevReason Reason, string OpKey) : IGameEvent;
