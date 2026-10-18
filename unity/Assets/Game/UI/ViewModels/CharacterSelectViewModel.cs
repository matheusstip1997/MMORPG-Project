#nullable enable
using System;
using System.Collections.Generic;
using Shared.Protocol.Auth;

namespace Game.UI.ViewModels
{
    /// <summary>One slot card (R-9.10-04): a character, or an empty slot.</summary>
    public sealed class SlotView
    {
        public SlotView(int slot, CharacterCard? card, int deletionDaysLeft)
        {
            Slot = slot;
            Card = card;
            DeletionDaysLeft = deletionDaysLeft;
        }

        public int Slot { get; }

        public CharacterCard? Card { get; }

        /// <summary>Whole days left in the grace period when the card is pending deletion, otherwise 0.</summary>
        public int DeletionDaysLeft { get; }
    }

    /// <summary>Time left in the soft delete grace period (R-2.1-05), counted on the server's clock.</summary>
    public static class GracePeriod
    {
        public const long MsPerDay = 86_400_000;

        /// <summary>Whole days left, rounded up (a few hours left shows as 1 day); 0 once the time has passed.</summary>
        public static int DaysLeft(long purgeAfterUtcMs, long serverNowUtcMs)
        {
            long left = purgeAfterUtcMs - serverNowUtcMs;
            return left <= 0 ? 0 : (int)Math.Min(int.MaxValue, (left + MsPerDay - 1) / MsPerDay);
        }
    }

    /// <summary>
    /// Character selection (spec 9.10, R-9.10-04): the slots of the realm from <see cref="CharacterListResponse"/>, the
    /// selected card, the slot counter and the delete confirmation. Rank, jail, jobs, honor, location and last played
    /// come with their systems. Whether a pending deletion can be restored is Open, so there is no restore action.
    /// </summary>
    public sealed class CharacterSelectViewModel : ViewModel
    {
        private List<SlotView> _slots = new List<SlotView>();

        public string ServerName { get; private set; } = "";

        public int RealmId { get; private set; }

        public IReadOnlyList<SlotView> Slots => _slots;

        public int SlotsPerRealm { get; private set; }

        public int UsedSlots { get; private set; }

        /// <summary>Slot number of the selected card, -1 for none.</summary>
        public int SelectedSlot { get; private set; } = -1;

        public LoginStatus Status { get; private set; } = LoginStatus.None;

        /// <summary>The delete confirmation dialog is open for <see cref="SelectedCard"/>.</summary>
        public bool ConfirmingDelete { get; private set; }

        public CharacterCard? SelectedCard
        {
            get
            {
                foreach (var slot in _slots)
                {
                    if (slot.Slot == SelectedSlot)
                        return slot.Card;
                }
                return null;
            }
        }

        public bool CanEnter => SelectedCard != null && !Status.IsBusy && !ConfirmingDelete;

        public bool CanDelete => SelectedCard != null && !SelectedCard.PendingDeletion && !Status.IsBusy && !ConfirmingDelete;

        public bool CanNavigate => !Status.IsBusy && !ConfirmingDelete;

        public void SetServer(string serverName, int realmId)
        {
            ServerName = serverName;
            RealmId = realmId;
            NotifyChanged();
        }

        /// <summary>Fills the slots from the Auth API list. Keeps the selection when that slot still has a character.</summary>
        public void SetCharacters(CharacterListResponse list)
        {
            int slotCount = list.SlotsPerRealm;
            foreach (var card in list.Characters)
                slotCount = Math.Max(slotCount, card.Slot + 1);

            var slots = new List<SlotView>(slotCount);
            int used = 0;
            for (int slot = 0; slot < slotCount; slot++)
            {
                CharacterCard? card = null;
                foreach (var candidate in list.Characters)
                {
                    if (candidate.Slot == slot)
                        card = candidate;
                }
                if (card != null)
                    used++;
                int days = card != null && card.PendingDeletion ? GracePeriod.DaysLeft(card.PurgeAfterUtcMs, list.ServerTimeUtcMs) : 0;
                slots.Add(new SlotView(slot, card, days));
            }

            _slots = slots;
            SlotsPerRealm = list.SlotsPerRealm;
            UsedSlots = used;
            ConfirmingDelete = false;
            if (SelectedCard == null)
                SelectedSlot = FirstCharacterSlot();
            NotifyChanged();
        }

        public void Select(int slot)
        {
            if (slot == SelectedSlot || !CanNavigate)
                return;
            SelectedSlot = slot;
            Status = LoginStatus.None;
            NotifyChanged();
        }

        /// <summary>Selects the slot of a character just created.</summary>
        public void SelectCharacter(long characterId)
        {
            foreach (var slot in _slots)
            {
                if (slot.Card != null && slot.Card.CharacterId == characterId)
                    SelectedSlot = slot.Slot;
            }
            NotifyChanged();
        }

        public void BeginDelete()
        {
            if (!CanDelete)
                return;
            ConfirmingDelete = true;
            Status = LoginStatus.None;
            NotifyChanged();
        }

        public void CancelDelete()
        {
            if (!ConfirmingDelete)
                return;
            ConfirmingDelete = false;
            NotifyChanged();
        }

        /// <summary>Closes the confirmation while the delete request runs.</summary>
        public void EndDeleteConfirmation()
        {
            ConfirmingDelete = false;
            NotifyChanged();
        }

        public void SetStatus(LoginStatus status)
        {
            Status = status;
            NotifyChanged();
        }

        private int FirstCharacterSlot()
        {
            foreach (var slot in _slots)
            {
                if (slot.Card != null)
                    return slot.Slot;
            }
            return _slots.Count > 0 ? _slots[0].Slot : -1;
        }
    }
}
