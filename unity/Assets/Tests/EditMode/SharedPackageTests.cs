using System.Linq;
using NUnit.Framework;
using Shared;

namespace Game.Tests.EditMode
{
    public class SharedPackageTests
    {
        [Test]
        public void Shared_package_compiles_as_Game_Shared()
        {
            Assert.AreEqual("Game.Shared", typeof(SharedAssemblyMarker).Assembly.GetName().Name);
        }

        [Test]
        public void Shared_does_not_reference_Unity()
        {
            // Architecture rule 7: Shared uses no Unity APIs.
            var unityReferences = typeof(SharedAssemblyMarker).Assembly.GetReferencedAssemblies()
                .Where(a => a.Name.StartsWith("Unity"));

            Assert.IsEmpty(unityReferences);
        }
    }
}