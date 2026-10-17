using GameServer.Persistence;

namespace GameServer.Tests.Persistence;

public sealed class PersistenceSettingsTests
{
    private static Func<string, string?> Env(Dictionary<string, string> values) => name => values.GetValueOrDefault(name);

    [Fact]
    public void ReadsConnectionStringsAndRealmFromTheEnvironment()
    {
        var settings = PersistenceSettings.From(Env(new()
        {
            [PersistenceSettings.GlobalVariable] = "Host=g;Database=global",
            [PersistenceSettings.RealmVariable] = "Host=r;Database=realm",
            [PersistenceSettings.RealmIdVariable] = "3",
        }));

        Assert.Equal(new PersistenceSettings("Host=g;Database=global", "Host=r;Database=realm", 3), settings);
    }

    [Theory]
    [InlineData(PersistenceSettings.GlobalVariable)]
    [InlineData(PersistenceSettings.RealmVariable)]
    [InlineData(PersistenceSettings.RealmIdVariable)]
    public void MissingVariableIsAnError(string missing)
    {
        var values = new Dictionary<string, string>
        {
            [PersistenceSettings.GlobalVariable] = "Host=g",
            [PersistenceSettings.RealmVariable] = "Host=r",
            [PersistenceSettings.RealmIdVariable] = "1",
        };
        values.Remove(missing);

        var error = Assert.Throws<InvalidOperationException>(() => PersistenceSettings.From(Env(values)));
        Assert.Contains(missing, error.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("abc")]
    public void RealmIdMustBePositive(string realmId)
    {
        Assert.Throws<InvalidOperationException>(() => PersistenceSettings.From(Env(new()
        {
            [PersistenceSettings.GlobalVariable] = "Host=g",
            [PersistenceSettings.RealmVariable] = "Host=r",
            [PersistenceSettings.RealmIdVariable] = realmId,
        })));
    }
}
