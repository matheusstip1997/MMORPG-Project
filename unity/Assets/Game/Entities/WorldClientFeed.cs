#nullable enable
using System;
using System.Collections.Generic;
using Game.Gameplay.Prediction;
using Shared.Data;
using Shared.Protocol;
using Shared.Simulation.Actions;
using UnityEngine;

namespace Game.Gameplay.Entities
{
    public delegate void HitsHandler(uint serverTick, ReadOnlySpan<HitEntry> hits);

    public delegate void ActionResultHandler(ushort actionNetId, byte skillSlot, ActionReason reason);

    /// <summary>A local chat line (<see cref="ChatLineMessage"/>): the name already went through <c>DisplayIdentity</c>.</summary>
    public delegate void ChatLineHandler(uint senderEntityId, string senderName, string text);

    /// <summary>
    /// What the world client knows, for the HUD (backlog I, spec 9.4): the world client (today the dev
    /// <c>PredictionDevClient</c>) creates one, raises its events as messages arrive and ticks run, and the HUD binds to them
    /// instead of reading the network. A plain fan-out: the connection has one listener, the feed has many. The HUD only
    /// reads what it gets here; it never changes the registry or the predictor.
    /// </summary>
    public sealed class WorldClientFeed
    {
        public WorldClientFeed(ConfigSet config, ActionCatalog catalog, EntityRegistry registry, LocalPlayerPredictor predictor,
            EntityViews? views, Camera? camera, double tickMs)
        {
            Config = config;
            Catalog = catalog;
            Registry = registry;
            Predictor = predictor;
            Views = views;
            Camera = camera;
            TickMs = tickMs;
        }

        public ConfigSet Config { get; }

        public ActionCatalog Catalog { get; }

        public EntityRegistry Registry { get; }

        public LocalPlayerPredictor Predictor { get; }

        public EntityViews? Views { get; }

        public Camera? Camera { get; }

        public double TickMs { get; }

        public EntityIdentities Identities { get; } = new EntityIdentities();

        /// <summary>The tick others are drawn at (interpolation clock), for world overlays; false until it runs.</summary>
        public bool HasRenderTick { get; private set; }

        public double RenderTick { get; private set; }

        /// <summary>A snapshot was applied to <see cref="Registry"/> (own resources, statuses and flags are current).</summary>
        public event Action? SnapshotApplied;

        /// <summary>Hits the server resolved on entities this client sees (backlog D3).</summary>
        public event HitsHandler? HitsReceived;

        /// <summary>A refused action or a parry result (spec 9.4 <c>ActionResult</c>).</summary>
        public event ActionResultHandler? ActionResultReceived;

        /// <summary>A player's display name arrived (<see cref="EntityIdentity"/>, already in <see cref="Identities"/>).</summary>
        public event Action<uint>? IdentityReceived;

        /// <summary>A local chat line from a player this client sees, or the echo of its own (R-6.5-01, spec 9.4).</summary>
        public event ChatLineHandler? ChatReceived;

        /// <summary>The server refused this client's chat line (<see cref="ChatRefusedMessage"/>).</summary>
        public event Action<ChatReason>? ChatRefused;

        /// <summary>
        /// Sends a checked chat line on the reliable channel; set by the world client. False when it is not connected.
        /// The HUD sends through <see cref="TrySendChat"/>, never the connection.
        /// </summary>
        public Func<string, bool>? ChatSender { get; set; }

        /// <summary>
        /// Set by the HUD while the chat input has focus: the world client then suspends the gameplay keys
        /// (<c>GameInput.Suspended</c>), so typing never moves or attacks.
        /// </summary>
        public bool TextInputActive { get; set; }

        public bool TrySendChat(string text) => ChatSender != null && ChatSender(text);

        public void RaiseChatLine(uint senderEntityId, string senderName, string text) =>
            ChatReceived?.Invoke(senderEntityId, senderName, text);

        public void RaiseChatRefused(ChatReason reason) => ChatRefused?.Invoke(reason);

        /// <summary>The predictor ran one client tick (cooldowns, charges and the own action state may have changed).</summary>
        public event Action? PredictedTick;

        /// <summary>The connection was lost: everything the HUD shows is stale.</summary>
        public event Action? Disconnected;

        public void RaiseSnapshotApplied() => SnapshotApplied?.Invoke();

        public void RaiseHits(uint serverTick, ReadOnlySpan<HitEntry> hits) => HitsReceived?.Invoke(serverTick, hits);

        public void RaiseActionResult(ushort actionNetId, byte skillSlot, ActionReason reason) =>
            ActionResultReceived?.Invoke(actionNetId, skillSlot, reason);

        public void RaiseIdentity(uint entityId, string name)
        {
            Identities.Set(entityId, name);
            IdentityReceived?.Invoke(entityId);
        }

        public void RaisePredictedTick() => PredictedTick?.Invoke();

        public void RaiseDisconnected()
        {
            Identities.Clear();
            HasRenderTick = false;
            Disconnected?.Invoke();
        }

        public void SetRenderTick(double renderTick)
        {
            RenderTick = renderTick;
            HasRenderTick = true;
        }
    }

    /// <summary>
    /// Player display names by entity id, from <see cref="EntityIdentity"/> (the server already applied
    /// <c>DisplayIdentity</c>, rule 8). Entity ids are never reused within a world, so a name never goes stale; the reliable
    /// name may arrive before or after the unreliable spawn, so names are kept until the connection ends, not removed on
    /// despawn.
    /// </summary>
    public sealed class EntityIdentities
    {
        private readonly Dictionary<uint, string> _names = new Dictionary<uint, string>();

        public int Count => _names.Count;

        public void Set(uint entityId, string name) => _names[entityId] = name;

        public bool TryGet(uint entityId, out string name)
        {
            if (_names.TryGetValue(entityId, out var found))
            {
                name = found;
                return true;
            }
            name = string.Empty;
            return false;
        }

        public void Clear() => _names.Clear();
    }

    /// <summary>A world client that offers a <see cref="WorldClientFeed"/> to the HUD (found on a scene object).</summary>
    public interface IWorldClientFeedSource
    {
        /// <summary>Null until the client loaded its config.</summary>
        WorldClientFeed? Feed { get; }
    }
}
