using GameServer.Host;

namespace GameServer.Tests.Host;

public class ServerSettingsTests
{
    private static readonly string Secret = Convert.ToBase64String(new byte[32]);

    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void Reads_every_setting_with_the_default_data_directory()
    {
        var settings = ServerSettings.Read(Env(
            (ServerSettings.TicketSecretVariable, Secret),
            (ServerSettings.TicketKeyIdVariable, "3"),
            (ServerSettings.RealmIdVariable, "1"),
            (ServerSettings.PortVariable, "7777")), out var errors);

        Assert.Empty(errors);
        Assert.NotNull(settings);
        Assert.Equal((3, 1, 7777), (settings.TicketKeyId, settings.RealmId, settings.Port));
        Assert.Equal(Path.Combine("data", "tables"), settings.DataDirectory);
        Assert.Equal("telemetry", settings.TelemetryDirectory);
        Assert.DoesNotContain(Secret, settings.ToString());
    }

    [Fact]
    public void Telemetry_directory_can_be_moved()
    {
        var settings = ServerSettings.Read(Env(
            (ServerSettings.TicketSecretVariable, Secret),
            (ServerSettings.TicketKeyIdVariable, "3"),
            (ServerSettings.RealmIdVariable, "1"),
            (ServerSettings.PortVariable, "7777"),
            (ServerSettings.TelemetryDirectoryVariable, "D:/mmorpg-telemetry")), out _);

        Assert.Equal("D:/mmorpg-telemetry", settings?.TelemetryDirectory);
    }

    [Fact]
    public void Missing_or_bad_settings_are_all_reported_and_nothing_starts()
    {
        var settings = ServerSettings.Read(Env(
            (ServerSettings.TicketSecretVariable, Convert.ToBase64String(new byte[16])),
            (ServerSettings.TicketKeyIdVariable, "256"),
            (ServerSettings.PortVariable, "port")), out var errors);

        Assert.Null(settings);
        Assert.Equal(4, errors.Count); // short secret, key id range, realm missing, port not a number
    }
}
