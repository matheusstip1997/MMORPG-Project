#nullable enable
using System;
using Shared.Protocol;

namespace Game.UI.ViewModels.LootBag
{
    /// <summary>
    /// The death loot bag window (spec 4.3 R-4.3-03, 9.13; WP-M4-9): the bag open now as the server last sent it
    /// (<see cref="LootBagContent"/>: its rows and what this player may do), the time left of the owner window and of the bag
    /// counted down on the display clock from when the contents came, and the one take waiting for its answer. The server decides
    /// every take; this only shows what it sent.
    /// </summary>
    public sealed class LootBagWindowViewModel : ViewModel
    {
        public bool Open { get; private set; }

        /// <summary>The bag shown; null while closed.</summary>
        public LootBagContent? Content { get; private set; }

        /// <summary>When <see cref="Content"/> came (display ms): its times left count down from there.</summary>
        public double ReceivedAtMs { get; private set; }

        /// <summary>
        /// The take that waits for its answer (after its commit); 0 for none. It outlives <see cref="Close"/> (review of #248): a take
        /// that commits after the window closed still gets its toast. Only a disconnect (<see cref="Reset"/>) drops it.
        /// </summary>
        public uint PendingTake { get; private set; }

        /// <summary>What the viewer could do when <see cref="PendingTake"/> was asked (its toast says what it did).</summary>
        public LootBagAccess PendingAccess { get; private set; }

        public uint BagEntityId => Content?.BagEntityId ?? 0;

        /// <summary>A take may be asked: open, nothing waiting, the viewer may take (not locked) and something is left.</summary>
        public bool CanTake => Open && PendingTake == 0 && Content != null && Content.Access != LootBagAccess.Locked
                               && Content.Access != LootBagAccess.None && Content.Rows.Length > 0;

        /// <summary>New contents: shown (the window opens on them); a <c>Closed</c> one for the bag shown closes the window.</summary>
        public void SetContent(LootBagContent content, double nowMs)
        {
            if (content.Closed)
            {
                if (Content != null && Content.BagEntityId == content.BagEntityId)
                    Close();
                return;
            }
            Content = content;
            ReceivedAtMs = nowMs;
            Open = true;
            NotifyChanged();
        }

        public void SetPendingTake(uint requestId)
        {
            PendingTake = requestId;
            PendingAccess = Content?.Access ?? LootBagAccess.None;
            NotifyChanged();
        }

        /// <summary>
        /// The answer to a request; the reason code of a refusal to show (null for none), and whether a take of this window was
        /// applied. A refused open of a bag not shown is still shown (the player pressed the key).
        /// </summary>
        public string? OnResult(LootBagResult result, out bool applied)
        {
            applied = false;
            bool ours = result.RequestId == PendingTake && PendingTake != 0;
            if (ours)
            {
                PendingTake = 0;
                NotifyChanged();
            }
            if (result.Reason != LootBagReason.None)
                return result.Op == LootBagOp.Close ? null : result.Reason.ToString();
            applied = ours && (result.Op == LootBagOp.TakeAll || result.Op == LootBagOp.TakeOne);
            return null;
        }

        /// <summary>The owner window's time left now (ms; 0 once it ended).</summary>
        public long WindowLeftMs(double nowMs) => Left(Content?.OwnerWindowLeftMs ?? 0, nowMs);

        /// <summary>The bag's time left before it crumbles (ms).</summary>
        public long ExpiresLeftMs(double nowMs) => Left(Content?.ExpiresInMs ?? 0, nowMs);

        /// <summary>The countdowns moved on (the binder calls it about once a second while open).</summary>
        public void Redraw()
        {
            if (Open)
                NotifyChanged();
        }

        public void Close()
        {
            if (!Open && Content == null)
                return; // a waiting take stays: its answer still toasts
            Open = false;
            Content = null;
            NotifyChanged();
        }

        /// <summary>A disconnect: closed, and no answer will come for a waiting take.</summary>
        public void Reset()
        {
            PendingTake = 0;
            PendingAccess = LootBagAccess.None;
            Close();
        }

        private long Left(uint leftAtReceipt, double nowMs) => Math.Max(0L, leftAtReceipt - (long)Math.Max(0, nowMs - ReceivedAtMs));
    }
}
