#nullable enable
using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Game.Dev.Qa.Tests
{
    /// <summary>
    /// The QA smoke run (backlog J): plays <see cref="QaScenario"/> against a locally running Auth API (dev login) and
    /// GameServer and writes screenshots and JSON results to <c>MMORPG_QA_RUN_DIR</c>. Skipped unless that variable is
    /// set; <c>tools/qa/Run-QaSmoke.ps1</c> starts the stack, sets it and runs this test (see <c>tools/qa/README.md</c>).
    /// </summary>
    public sealed class QaSmokeScenarioTests
    {
        public const string RunDirVariable = "MMORPG_QA_RUN_DIR";

        [UnityTest, Timeout(600_000)]
        public IEnumerator Smoke_scenario()
        {
            var runDir = Environment.GetEnvironmentVariable(RunDirVariable);
            if (string.IsNullOrEmpty(runDir))
            {
                Assert.Ignore($"{RunDirVariable} is not set; see tools/qa/README.md");
                yield break;
            }
            Assert.That(QaScenario.TryCreate(runDir!, out var scenario, out var error), Is.True, error);
            using (scenario)
            {
                yield return scenario!.Run();
                var result = scenario.Result;
                var failed = result.steps.FirstOrDefault(step => step.status == QaStatus.Fail);
                Assert.That(result.passed, Is.True,
                    failed != null ? $"step {failed.index} {failed.name}: {failed.message}" : "no step passed");
            }
        }
    }
}
