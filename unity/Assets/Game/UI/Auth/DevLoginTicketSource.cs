#if UNITY_EDITOR || MMORPG_DEV_LOGIN
#nullable enable
using System;
using System.Globalization;
using System.Threading.Tasks;
using Game.UI.Config;

namespace Game.UI.Auth
{
    /// <summary>
    /// Dev login (spec 2.1 implementation notes): the ticket <c>dev:&lt;n&gt;</c>, which an Auth API built with the
    /// <c>DevLogin</c> flag and run with <c>MMORPG_AUTH_IDENTITY=dev</c> accepts as fake SteamID n. Compiled only in
    /// the editor and in builds with the <c>MMORPG_DEV_LOGIN</c> scripting define, like <c>DevTicket</c>.
    /// </summary>
    public sealed class DevLoginTicketSource : ILoginTicketSource
    {
        public const string Prefix = "dev:";
        public const string CommandLineOption = "-devLogin";

        private readonly long _devSteamId;

        public DevLoginTicketSource(long devSteamId)
        {
            if (devSteamId <= 0)
                throw new ArgumentOutOfRangeException(nameof(devSteamId), "The dev SteamID must be positive.");
            _devSteamId = devSteamId;
        }

        /// <summary><c>-devLogin &lt;n&gt;</c> on the command line (several clients on one machine), else <paramref name="fallback"/>.</summary>
        public static DevLoginTicketSource FromCommandLine(string[] args, long fallback)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == CommandLineOption
                    && long.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
                    return new DevLoginTicketSource(id);
            }
            return new DevLoginTicketSource(fallback);
        }

        public Task<LoginTicket> GetTicketAsync()
        {
            var id = _devSteamId.ToString(CultureInfo.InvariantCulture);
            return Task.FromResult(LoginTicket.Issued(Prefix + id, UiTextIds.BadgeDev, new TextArg("id", id)));
        }
    }
}
#endif
