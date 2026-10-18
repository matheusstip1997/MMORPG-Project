#nullable enable
using System;
using System.Collections.Generic;
using Game.Net;
using Shared.Data;
using Shared.Data.Tables;

namespace Game.UI.Config
{
    /// <summary>The config tables the login screens read (spec 9.10), taken from a loaded and validated <see cref="ConfigSet"/>.</summary>
    public sealed class LoginConfig
    {
        private readonly Dictionary<string, LoginLayout> _layout = new Dictionary<string, LoginLayout>(StringComparer.Ordinal);

        public LoginConfig(
            LoginSettings settings,
            IReadOnlyList<LoginScreens> screens,
            IEnumerable<LoginLayout> layout,
            IReadOnlyList<ServerDirectory> servers,
            CharacterCreationConfig creation,
            NetSecurity netSecurity,
            UiText text,
            ReasonText reasons)
        {
            Settings = settings;
            Screens = screens;
            Servers = servers;
            Creation = creation;
            NetLimits = new ClientNetLimits(netSecurity.MaxReliableMessageBytes, netSecurity.MaxUnreliableMessageBytes);
            Text = text;
            Reasons = reasons;
            foreach (var row in layout)
                _layout[row.Id] = row;
        }

        public LoginSettings Settings { get; }

        public IReadOnlyList<LoginScreens> Screens { get; }

        public IReadOnlyList<ServerDirectory> Servers { get; }

        public CharacterCreationConfig Creation { get; }

        /// <summary>Message size caps for the game connection, from <c>NetSecurity</c> (R-2.5-03).</summary>
        public ClientNetLimits NetLimits { get; }

        public UiText Text { get; }

        public ReasonText Reasons { get; }

        public bool TryGetLayout(string screen, string element, out LoginLayout row) =>
            _layout.TryGetValue(LoginLayout.IdFor(screen, element), out row!);

        public static LoginConfig From(ConfigSet set) => new LoginConfig(
            set.Single<LoginSettings>(),
            Rows<LoginScreens>(set),
            Rows<LoginLayout>(set),
            Rows<ServerDirectory>(set),
            set.Single<CharacterCreationConfig>(),
            set.Single<NetSecurity>(),
            new UiText(Rows<UiStrings>(set)),
            new ReasonText(Rows<ReasonCodeStrings>(set)));

        private static List<T> Rows<T>(ConfigSet set)
        {
            var rows = new List<T>();
            if (set.TryGet(typeof(T), out var table))
            {
                foreach (var row in table.Rows)
                    rows.Add((T)row);
            }
            return rows;
        }
    }
}
