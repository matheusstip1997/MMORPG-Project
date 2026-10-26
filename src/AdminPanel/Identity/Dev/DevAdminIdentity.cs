using GameServer.Admin;

namespace AdminPanel.Identity.Dev;

/// <summary>
/// Dev admin sign-in (M1, spec 7.4 implementation). Compiled only with the DevLogin build flag and used only when
/// <c>MMORPG_ADMIN_IDENTITY=dev</c>: any name signs in, with no password, and is recorded as <c>dev:&lt;name&gt;</c>.
/// Anyone who can reach the panel is an admin: it listens on loopback only (dev containers: published on 127.0.0.1).
/// </summary>
public sealed class DevAdminIdentity : IAdminIdentity
{
    public const string Prefix = "dev:";

    public AdminReasonCode Verify(string? name, int maxChars, out string adminName)
    {
        adminName = "";
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || Prefix.Length + name.Length > maxChars || !name.All(IsNameChar))
            return AdminReasonCode.RequestInvalid;
        adminName = Prefix + name;
        return AdminReasonCode.None;
    }

    private static bool IsNameChar(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.';
}
