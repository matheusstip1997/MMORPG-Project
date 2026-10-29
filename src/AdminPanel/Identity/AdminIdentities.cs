using GameServer.Admin;

namespace AdminPanel;

/// <summary>Who signs in to the panel. M1 has only the dev identity; production admin auth is TODO(open) (spec 7.4).</summary>
public interface IAdminIdentity
{
    /// <summary>The admin name recorded in <c>admin_actions</c>, or the reason the sign-in is refused.</summary>
    AdminReasonCode Verify(string? name, int maxChars, out string adminName);
}

/// <summary>Picks the identity named by <c>MMORPG_ADMIN_IDENTITY</c>.</summary>
public static class AdminIdentities
{
    public const string None = "none";
    public const string Dev = "dev";

    /// <summary>True when this build contains the dev admin identity (DevLogin.props, Debug only).</summary>
    public const bool DevCompiled =
#if DEV_LOGIN
        true;
#else
        false;
#endif

    public static bool IsAvailable(string mode, out string error)
    {
        error = "";
        if (mode == None || (mode == Dev && DevCompiled))
            return true;
        error = mode == Dev
            ? "the dev admin identity is not compiled into this build (DevLogin=false, the Release default)"
            : $"unknown admin identity '{mode}' (expected '{None}' or '{Dev}')";
        return false;
    }

    public static IAdminIdentity Create(string mode) => mode switch
    {
        None => new NoAdminIdentity(),
#if DEV_LOGIN
        Dev => new Identity.Dev.DevAdminIdentity(),
#endif
        _ => throw new InvalidOperationException($"admin identity '{mode}' is not available in this build"),
    };

    /// <summary>No sign-in at all: the default, and the only choice in Release builds until production admin auth exists.</summary>
    private sealed class NoAdminIdentity : IAdminIdentity
    {
        public AdminReasonCode Verify(string? name, int maxChars, out string adminName)
        {
            adminName = "";
            return AdminReasonCode.SignInUnavailable;
        }
    }
}
