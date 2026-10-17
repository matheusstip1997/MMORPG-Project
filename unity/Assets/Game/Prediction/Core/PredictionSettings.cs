#nullable enable
using System;
using Shared.Data;
using Shared.Data.Tables;

namespace Game.Gameplay.Prediction
{
    /// <summary>
    /// The config values client prediction and interpolation run on, read from the same tables as the server
    /// (spec 1.8): tick rate, speed, redundancy and interpolation delay from <see cref="StartingValuesM1"/>, client-only
    /// values from <see cref="ClientPrediction"/>. Plain C# with no UnityEngine API (GameServer.Tests compiles it).
    /// </summary>
    public readonly struct PredictionSettings
    {
        public PredictionSettings(StartingValuesM1 values, ClientPrediction client)
        {
            TickRateHz = values.TickRateHz;
            MoveSpeedCmPerSec = values.MoveSpeedCmPerSec;
            InputRedundancy = values.InputRedundancy;
            InterpolationDelayMs = values.InterpolationDelayMs;
            CorrectionToleranceCm = client.CorrectionToleranceCm;
            CorrectionSnapCm = client.CorrectionSnapCm;
            CorrectionSmoothingMs = client.CorrectionSmoothingMs;
            MaxPendingInputs = client.MaxPendingInputs;
            MaxTicksPerFrame = client.MaxTicksPerFrame;
            InterpolationClockCatchUpMs = client.InterpolationClockCatchUpMs;
            if (TickRateHz <= 0 || MaxPendingInputs <= 0 || MaxTicksPerFrame <= 0 || InputRedundancy <= 0
                || InterpolationClockCatchUpMs <= 0)
                throw new ArgumentException("Config values must be validated before use (TableValidator).");
        }

        public static PredictionSettings From(ConfigSet set) =>
            new PredictionSettings(set.Single<StartingValuesM1>(), set.Single<ClientPrediction>());

        public int TickRateHz { get; }
        public int MoveSpeedCmPerSec { get; }
        public int InputRedundancy { get; }
        public int InterpolationDelayMs { get; }
        public int CorrectionToleranceCm { get; }
        public int CorrectionSnapCm { get; }
        public int CorrectionSmoothingMs { get; }
        public int MaxPendingInputs { get; }
        public int MaxTicksPerFrame { get; }
        public int InterpolationClockCatchUpMs { get; }

        public double TickMs => 1000.0 / TickRateHz;
    }
}
