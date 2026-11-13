using GameServer.Entities;
using GameServer.Outposts;
using Shared.Protocol;

namespace GameServer.Items.Mail;

/// <summary>
/// The mailbox's interaction (spec 4.5 R-4.5-03, M4 decision 6a; WP-M4-14): <see cref="OutpostInteractions"/> checks the mailbox
/// exists, is seen and is in reach; the yes lets the client open its mail window there. Nothing else happens here: the list
/// and a mail are read anywhere, and every claim runs the same reach check again (<c>GameWorld.HandleMailRequest</c>).
/// </summary>
public sealed class MailboxInteraction : IOutpostInteractionHandler
{
    public OutpostReason? Interact(Player player, Npc npc, uint requestId) => OutpostReason.None;
}
