#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data.Tables;

namespace Game.UI.ViewModels
{
    /// <summary>One row of the server table (R-9.10-02).</summary>
    public sealed class ServerRowView
    {
        public ServerRowView(ServerDirectory server, int yourCharacters)
        {
            Server = server;
            YourCharacters = yourCharacters;
        }

        public ServerDirectory Server { get; }

        /// <summary>The account's character count on this server; -1 when unknown.</summary>
        public int YourCharacters { get; }

        /// <summary>Maintenance servers are listed but not selectable (R-9.10-02).</summary>
        public bool Selectable => Server.Status != ServerStatus.Maintenance;
    }

    /// <summary>
    /// Server selection (spec 9.10). TODO(B5-serverlist): population and ping (R-9.10-02) come with
    /// <c>ServerListResponse</c> and a ping probe; until then the table shows them as unknown.
    /// </summary>
    public sealed class ServerListViewModel : ViewModel
    {
        private List<ServerRowView> _rows = new List<ServerRowView>();

        public IReadOnlyList<ServerRowView> Rows => _rows;

        public int SelectedIndex { get; private set; } = -1;

        public ServerRowView? Selected => SelectedIndex >= 0 && SelectedIndex < _rows.Count ? _rows[SelectedIndex] : null;

        public LoginStatus Status { get; private set; } = LoginStatus.None;

        public bool CanEnter => Selected != null && Selected.Selectable && !Status.IsBusy;

        /// <param name="yourCharacters">Character count for a realm id, or -1 when unknown.</param>
        public void SetServers(IEnumerable<ServerDirectory> servers, Func<int, int> yourCharacters)
        {
            var previous = Selected?.Server.Id;
            _rows = new List<ServerRowView>();
            foreach (var server in servers)
                _rows.Add(new ServerRowView(server, yourCharacters(server.RealmId)));

            SelectedIndex = -1;
            for (int i = 0; i < _rows.Count && SelectedIndex < 0; i++)
            {
                if (_rows[i].Server.Id == previous)
                    SelectedIndex = i;
            }
            for (int i = 0; i < _rows.Count && SelectedIndex < 0; i++)
            {
                if (_rows[i].Selectable)
                    SelectedIndex = i;
            }
            NotifyChanged();
        }

        public void Select(int index)
        {
            if (index == SelectedIndex || index < 0 || index >= _rows.Count || Status.IsBusy)
                return;
            SelectedIndex = index;
            Status = LoginStatus.None;
            NotifyChanged();
        }

        public void SetStatus(LoginStatus status)
        {
            Status = status;
            NotifyChanged();
        }
    }
}
