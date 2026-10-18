using System;
using System.IO;
using Game.Gameplay.Streaming;
using NUnit.Framework;
using Shared.Simulation;
using Shared.World;

namespace Game.Tests.EditMode.Streaming
{
    /// <summary>The client loads the world the server simulates and verifies it like the server (spec 11.6).</summary>
    public class ClientWorldTests
    {
        private string _copy = string.Empty;

        [TearDown]
        public void DeleteCopy()
        {
            if (_copy.Length > 0 && Directory.Exists(_copy))
                Directory.Delete(_copy, recursive: true);
        }

        [Test]
        public void The_committed_M1_world_loads_from_the_repository()
        {
            Assert.IsTrue(ClientWorld.TryLoadGrid("m1", out var grid, out string error), error);

            // Spawn marker m1.spawn.city and the city wall (data/world/m1/markers.json, blockers.json).
            Assert.IsTrue(grid!.IsWalkable(FixVec2.FromCm(-3_000, 0)));
            Assert.IsFalse(grid.IsWalkable(FixVec2.FromCm(-1_000, 1_000)));
        }

        [Test]
        public void A_world_changed_after_export_is_refused()
        {
            _copy = Path.Combine(Path.GetTempPath(), "mmorpg-client-world-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_copy);
            foreach (var file in Directory.GetFiles(ClientWorld.Directory("m1")))
                File.Copy(file, Path.Combine(_copy, Path.GetFileName(file)));
            var gridPath = Path.Combine(_copy, WorldFiles.Grid);
            var bytes = File.ReadAllBytes(gridPath);
            bytes[bytes.Length - 1] ^= 0x01;
            File.WriteAllBytes(gridPath, bytes);

            Assert.IsFalse(ClientWorld.TryLoadGridFrom(_copy, out var grid, out string error));
            Assert.IsNull(grid);
            StringAssert.Contains("SHA-256", error);
        }

        [Test]
        public void An_unknown_world_is_refused_with_its_directory()
        {
            Assert.IsFalse(ClientWorld.TryLoadGrid("no-such-world", out _, out string error));
            StringAssert.Contains(ClientWorld.Directory("no-such-world"), error);
        }
    }
}
