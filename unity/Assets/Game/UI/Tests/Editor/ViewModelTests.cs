#nullable enable
using System;
using Game.Net;
using Game.UI.Login;
using Game.UI.ViewModels;
using NUnit.Framework;
using Shared.Data.Tables;
using Shared.Protocol;
using static Game.UI.Tests.LoginTestData;

namespace Game.UI.Tests
{
    /// <summary>View models (spec 9.9): state from messages, one Changed event per update, no rules.</summary>
    public sealed class ViewModelTests
    {
        [TestCase(10_000, 10_000, 0)]
        [TestCase(10_000, 20_000, 0)]
        [TestCase(10_001, 10_000, 1)]
        [TestCase(GracePeriod.MsPerDay * 7, 0, 7)]
        [TestCase(GracePeriod.MsPerDay * 7 - 1, 0, 7)]
        [TestCase(GracePeriod.MsPerDay * 7 + 1, 0, 8)]
        public void Days_left_round_up_on_the_server_clock(long purgeAfter, long now, int days)
        {
            Assert.That(GracePeriod.DaysLeft(purgeAfter, now), Is.EqualTo(days));
        }

        [Test]
        public void Character_slots_fill_from_the_list_with_empty_slots()
        {
            var vm = new CharacterSelectViewModel();
            int changes = 0;
            vm.Changed += () => changes++;

            vm.SetCharacters(List(Card(5, 3, "Last")));

            Assert.That(changes, Is.EqualTo(1));
            Assert.That(vm.Slots.Count, Is.EqualTo(4));
            Assert.That(vm.SlotsPerRealm, Is.EqualTo(4));
            Assert.That(vm.UsedSlots, Is.EqualTo(1));
            Assert.That(vm.SelectedSlot, Is.EqualTo(3), "the first character is selected");
            Assert.That(vm.SelectedCard!.Name, Is.EqualTo("Last"));
        }

        [Test]
        public void A_card_beyond_the_slot_count_is_still_shown()
        {
            var vm = new CharacterSelectViewModel();

            vm.SetCharacters(List(Card(5, 5)));

            Assert.That(vm.Slots.Count, Is.EqualTo(6));
            Assert.That(vm.SlotsPerRealm, Is.EqualTo(4));
        }

        [Test]
        public void The_selection_is_kept_when_its_character_is_still_there()
        {
            var vm = new CharacterSelectViewModel();
            vm.SetCharacters(List(Card(1, 0), Card(2, 1)));
            vm.Select(1);

            vm.SetCharacters(List(Card(1, 0), Card(2, 1, "Renamed")));

            Assert.That(vm.SelectedSlot, Is.EqualTo(1));
        }

        [Test]
        public void An_empty_selection_cannot_enter_or_delete()
        {
            var vm = new CharacterSelectViewModel();
            vm.SetCharacters(List());

            Assert.That(vm.SelectedSlot, Is.EqualTo(0));
            Assert.That(vm.SelectedCard, Is.Null);
            Assert.That(vm.CanEnter, Is.False);
            Assert.That(vm.CanDelete, Is.False);
            vm.BeginDelete();
            Assert.That(vm.ConfirmingDelete, Is.False);
        }

        [Test]
        public void Busy_blocks_selection_and_actions()
        {
            var vm = new CharacterSelectViewModel();
            vm.SetCharacters(List(Card(1, 0), Card(2, 1)));
            vm.SetStatus(LoginStatus.Busy("x"));

            vm.Select(1);

            Assert.That(vm.SelectedSlot, Is.EqualTo(0));
            Assert.That(vm.CanEnter || vm.CanDelete || vm.CanNavigate, Is.False);
        }

        [Test]
        public void Server_selection_starts_on_the_first_selectable_server()
        {
            var vm = new ServerListViewModel();

            vm.SetServers(new[] { Server("a", 1, ServerStatus.Maintenance), Server("b", 2) }, _ => -1);

            Assert.That(vm.SelectedIndex, Is.EqualTo(1));
            Assert.That(vm.CanEnter, Is.True);
            vm.Select(0);
            Assert.That(vm.CanEnter, Is.False, "maintenance is listed but not selectable (R-9.10-02)");
        }

        [Test]
        public void Selecting_the_same_row_raises_nothing()
        {
            var vm = new ServerListViewModel();
            vm.SetServers(new[] { Server() }, _ => 0);
            int changes = 0;
            vm.Changed += () => changes++;

            vm.Select(0);
            vm.Select(5);

            Assert.That(changes, Is.EqualTo(0));
        }

        [Test]
        public void Create_needs_a_name_and_a_class()
        {
            var vm = new CharacterCreateViewModel();
            vm.Open(new[] { "warrior" }, 16, Guid.NewGuid());

            Assert.That(vm.CanCreate, Is.False);
            vm.SetName("Aria");
            Assert.That(vm.CanCreate, Is.True);
            vm.SetStatus(LoginStatus.Busy("x"));
            Assert.That(vm.CanCreate, Is.False);
            vm.SetName("Other");
            Assert.That(vm.Name, Is.EqualTo("Aria"), "no edits while the request runs");
        }

        [Test]
        public void Opening_create_again_starts_a_new_request()
        {
            var vm = new CharacterCreateViewModel();
            var first = Guid.NewGuid();
            vm.Open(new[] { "warrior" }, 16, first);
            vm.SetName("Aria");

            vm.Open(new[] { "warrior" }, 16, Guid.NewGuid());

            Assert.That(vm.RequestId, Is.Not.EqualTo(first));
            Assert.That(vm.Name, Is.Empty);
        }

        [TestCase(ClientDisconnectReason.Rejected, ConnectRejectReason.ServerFull, "ServerFull")]
        [TestCase(ClientDisconnectReason.Rejected, ConnectRejectReason.None, "ConnectionRejected")]
        [TestCase(ClientDisconnectReason.Unreachable, ConnectRejectReason.None, "GameServerUnreachable")]
        [TestCase(ClientDisconnectReason.Timeout, ConnectRejectReason.None, "ConnectionTimeout")]
        [TestCase(ClientDisconnectReason.ClosedByServer, ConnectRejectReason.None, "ConnectionLost")]
        [TestCase(ClientDisconnectReason.NetworkError, ConnectRejectReason.None, "ConnectionLost")]
        [TestCase(ClientDisconnectReason.SecurityViolation, ConnectRejectReason.None, "ConnectionSecurityViolation")]
        [TestCase(ClientDisconnectReason.ClosedByClient, ConnectRejectReason.None, "")]
        public void Connection_failures_map_to_reason_codes(ClientDisconnectReason reason, ConnectRejectReason reject, string code)
        {
            Assert.That(ConnectFailure.ReasonFor(reason, reject), Is.EqualTo(code));
        }

        [Test]
        public void Every_reject_reason_maps_to_its_own_name()
        {
            foreach (ConnectRejectReason reject in Enum.GetValues(typeof(ConnectRejectReason)))
            {
                if (reject != ConnectRejectReason.None)
                    Assert.That(ConnectFailure.ReasonFor(ClientDisconnectReason.Rejected, reject), Is.EqualTo(reject.ToString()));
            }
        }

        [Test]
        public void World_handoff_is_taken_once()
        {
            var relay = new ClientConnectionRelay();
            using var connection = new ClientConnection(relay, new ClientNetLimits(1024, 512));
            WorldHandoff.Offer(new WorldEntry(connection, relay, 1, 11));

            Assert.That(WorldHandoff.TryTake(out var entry), Is.True);
            Assert.That(entry!.CharacterId, Is.EqualTo(11));
            Assert.That(WorldHandoff.TryTake(out _), Is.False);
        }
    }
}
