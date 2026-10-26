#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Game.Dev.Qa
{
    /// <summary>A scenario step failed: the message says what was expected and what happened.</summary>
    public sealed class QaFailure : Exception
    {
        public QaFailure(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// A step does not apply to this run (for example the arrow at the wall for a class whose kit shoots nothing): it is
    /// reported <see cref="QaStatus.NotApplicable"/> with the reason, does not fail the run, and the next step runs.
    /// </summary>
    public sealed class QaNotApplicable : Exception
    {
        public QaNotApplicable(string message) : base(message)
        {
        }
    }

    public static class QaStatus
    {
        public const string Pass = "pass";
        public const string Fail = "fail";
        public const string Skipped = "skipped";
        /// <summary>The step does not apply to this run's class (<see cref="QaNotApplicable"/>); the run can still pass.</summary>
        public const string NotApplicable = "n/a";
    }

    /// <summary>One check of a step, with what was expected and what was seen (both as text).</summary>
    [Serializable]
    public sealed class QaCheck
    {
        public string name = "";
        public bool pass;
        public string expected = "";
        public string actual = "";
    }

    /// <summary>The JSON of one step (<c>steps/NN-name.json</c>).</summary>
    [Serializable]
    public sealed class QaStepResult
    {
        public int index;
        public string name = "";
        public string status = QaStatus.Skipped;
        public string startedUtc = "";
        public long durationMs;
        public string message = "";
        public List<string> screenshots = new List<string>();
        public List<QaCheck> checks = new List<QaCheck>();
        public List<string> log = new List<string>();

        public bool Passed => status == QaStatus.Pass;

        /// <summary>Passed, or not applicable to this run: the next step runs and the run can pass.</summary>
        public bool Cleared => status == QaStatus.Pass || status == QaStatus.NotApplicable;
    }

    /// <summary>The JSON of the whole run (<c>result.json</c>).</summary>
    [Serializable]
    public sealed class QaRunResult
    {
        public string runDir = "";
        public string startedUtc = "";
        public long durationMs;
        public bool passed;
        public string devLogin = "";
        /// <summary>The character's class (<c>ClassDef</c> id) the scenario played, from <c>-qaClass</c> or the first allowed class.</summary>
        public string classId = "";
        public string failedStep = "";

        /// <summary>
        /// The bag the move-stack step left (WP16): <c>Run-QaSmoke.ps1</c> checks that the database holds
        /// <see cref="bagCount"/> of <see cref="bagItemId"/> in <see cref="bagSlot"/> and nothing of it in <see cref="bagFromSlot"/>.
        /// Empty item id: the step did not get that far.
        /// </summary>
        public string bagItemId = "";
        public int bagSlot = -1;
        public int bagFromSlot = -1;
        public int bagCount;
        public List<QaStepResult> steps = new List<QaStepResult>();
    }

    /// <summary>
    /// Writes the QA smoke run's results under the run folder: <c>steps/NN-name.json</c> and <c>.png</c> per step and
    /// <c>result.json</c> for the run. Paths in the JSON are relative to the run folder.
    /// </summary>
    public sealed class QaRunWriter
    {
        public const string StepsFolder = "steps";
        public const string ResultFile = "result.json";

        private readonly string _runDir;

        public QaRunWriter(string runDir, IReadOnlyList<string> stepNames, string startedUtc)
        {
            _runDir = runDir;
            Result = new QaRunResult { runDir = runDir, startedUtc = startedUtc };
            for (int i = 0; i < stepNames.Count; i++)
                Result.steps.Add(new QaStepResult { index = i + 1, name = stepNames[i] });
            Directory.CreateDirectory(Path.Combine(runDir, StepsFolder));
        }

        public QaRunResult Result { get; }

        /// <summary>Relative path of a step's screenshot: <c>steps/NN-name.png</c>, or <c>NN-name-suffix.png</c>.</summary>
        public static string ShotPath(QaStepResult step, string suffix = "") =>
            StepsFolder + "/" + FilePrefix(step) + (suffix.Length > 0 ? "-" + suffix : "") + ".png";

        public static string FilePrefix(QaStepResult step) =>
            step.index.ToString("00", CultureInfo.InvariantCulture) + "-" + step.name;

        public string FullPath(string relative) => Path.Combine(_runDir, relative.Replace('/', Path.DirectorySeparatorChar));

        public void WriteStep(QaStepResult step) =>
            File.WriteAllText(FullPath(StepsFolder + "/" + FilePrefix(step) + ".json"), JsonUtility.ToJson(step, true));

        /// <summary>Marks every step that never ran as skipped, works out the verdict and writes <c>result.json</c>.</summary>
        public void Finish(long durationMs)
        {
            Result.durationMs = durationMs;
            Result.passed = Result.steps.Count > 0;
            foreach (var step in Result.steps)
            {
                if (step.Cleared)
                    continue;
                Result.passed = false;
                if (step.status == QaStatus.Fail && Result.failedStep.Length == 0)
                    Result.failedStep = step.name;
                if (step.status == QaStatus.Skipped)
                {
                    step.message = step.message.Length > 0 ? step.message : "not run: an earlier step failed";
                    WriteStep(step);
                }
            }
            File.WriteAllText(FullPath(ResultFile), JsonUtility.ToJson(Result, true));
        }
    }
}
