using System.Reflection;

namespace GameServer.Tests;

public class GameServerAssemblyTests
{
    [Fact]
    public void References_Shared()
    {
        var references = Assembly.Load("GameServer").GetReferencedAssemblies().Select(a => a.Name);

        Assert.Contains("Shared", references);
    }
}
