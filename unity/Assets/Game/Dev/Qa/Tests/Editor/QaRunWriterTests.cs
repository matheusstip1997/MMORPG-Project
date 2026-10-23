#nullable enable
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Game.Dev.Qa.EditorTests
{
    public sealed class QaRunWriterTests
    {
        private string _dir = "";

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "qa-writer-" + System.Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }

        [Test]
        public void Steps_after_a_failure_are_written_as_skipped()
        {
            var writer = new QaRunWriter(_dir, new[] { "title", "dev-login", "server-select" }, "2026-10-22T00:00:00Z");
            var first = writer.Result.steps[0];
            first.status = QaStatus.Pass;
            writer.WriteStep(first);
            var second = writer.Result.steps[1];
            second.status = QaStatus.Fail;
            second.message = "timed out";
            writer.WriteStep(second);

            writer.Finish(1234);

            var result = JsonUtility.FromJson<QaRunResult>(File.ReadAllText(Path.Combine(_dir, QaRunWriter.ResultFile)));
            Assert.That(result.passed, Is.False);
            Assert.That(result.failedStep, Is.EqualTo("dev-login"));
            Assert.That(result.durationMs, Is.EqualTo(1234));
            Assert.That(result.steps[2].status, Is.EqualTo(QaStatus.Skipped));
            Assert.That(File.Exists(Path.Combine(_dir, "steps", "03-server-select.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(_dir, "steps", "01-title.json")), Is.True);
        }

        [Test]
        public void All_passed_steps_pass_the_run()
        {
            var writer = new QaRunWriter(_dir, new[] { "title" }, "2026-10-22T00:00:00Z");
            writer.Result.steps[0].status = QaStatus.Pass;
            writer.Finish(1);
            Assert.That(writer.Result.passed, Is.True);
            Assert.That(writer.Result.failedStep, Is.Empty);
        }

        [Test]
        public void Screenshot_paths_are_relative_and_numbered()
        {
            var step = new QaStepResult { index = 7, name = "kill-demon" };
            Assert.That(QaRunWriter.ShotPath(step), Is.EqualTo("steps/07-kill-demon.png"));
            Assert.That(QaRunWriter.ShotPath(step, "fight"), Is.EqualTo("steps/07-kill-demon-fight.png"));
        }
    }
}
