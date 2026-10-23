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

    public static class QaStatus
    {
        public const string Pass = "pass";
        public const string Fail = "fail";
        public const string Skipped = "skipped";
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
        public string failedStep = "";
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
                if (step.Passed)
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
