#if UNITY_EDITOR || MMORPG_DEV_LOGIN
#nullable enable
using Shared.Data.Tables;
using UnityEngine;

namespace Game.Gameplay.Dev
{
    /// <summary>
    /// The server's tick timing in the dev scene (spec 11.7, backlog J); the <c>Hotkeys</c> row <c>devTickTiming</c> (F6)
    /// shows or hides it. Read from the server's own per-tick <c>TickTiming</c> telemetry (the same source as the bot load
    /// test's tick p99), over the last <c>DevTools.tickWindowMs</c>: p50, p99 and worst tick time, ticks over the tick
    /// budget, the worst start lateness, the overruns, plus the last table reload on both sides. The server writes
    /// <c>TickTiming</c> only with <c>TelemetrySettings.tickTimingLog</c> on. Dev text (IMGUI), never part of the HUD.
    /// Compiled with the dev client.
    /// </summary>
    public sealed class TickTimingView : MonoBehaviour
    {
        [SerializeField] private PredictionDevClient? client;
        [SerializeField] private DevTelemetryFeed? telemetry;
        [SerializeField] private DevTableReload? reload;
        [Tooltip("Look only: how often the readout is summarised, in seconds.")]
        [SerializeField] private float refreshS = 0.25f;

        [SerializeField] private bool shown;

        private TickTimingWindow? _window;
        private DevKey? _key;
        private TickTimingSummary _summary;
        private float _nextSummary;

        public bool Shown => shown;

        /// <summary>QA counter: ticks in the window.</summary>
        public int Ticks => _window?.Count ?? 0;

        public TickTimingSummary Summary => _summary;

        private void Start()
        {
            if (client == null)
                client = FindAnyObjectByType<PredictionDevClient>();
            if (telemetry == null)
                telemetry = FindAnyObjectByType<DevTelemetryFeed>();
            if (reload == null)
                reload = FindAnyObjectByType<DevTableReload>();
            var config = client?.Config;
            if (config == null || telemetry == null)
            {
                Debug.LogError("[DevTools] tick timing: no dev client with tables or telemetry feed in the scene");
                enabled = false;
                return;
            }
            int tickRate = config.Single<StartingValuesM1>().TickRateHz;
            _window = new TickTimingWindow(TickTimingWindow.CapacityFor(config.Single<DevTools>().TickWindowMs, tickRate),
                TickTimingWindow.BudgetUsFor(tickRate));
            _key = DevKey.From(config, Hotkeys.DevTickTiming);
            telemetry.Received += OnTelemetry;
        }

        private void OnTelemetry(DevTelemetryRecord record)
        {
            if (record.Kind == DevTelemetryKind.TickTiming)
                _window!.AddTick((uint)record.V0, record.V1, record.V2);
            else if (record.Kind == DevTelemetryKind.TickStats)
                _window!.AddStats(record.V2);
        }

        private void Update()
        {
            if (_key != null && _key.WasPressedThisFrame())
                shown = !shown;
            // The summary sorts the window: a few times a second is plenty for a readout.
            if (shown && _window != null && Time.unscaledTime >= _nextSummary)
            {
                _summary = _window.Summarize();
                _nextSummary = Time.unscaledTime + refreshS;
            }
        }

        private void OnGUI()
        {
            if (!shown || _window == null)
                return;
            var s = _summary;
            string file = telemetry?.CurrentFile != null ? System.IO.Path.GetFileName(telemetry.CurrentFile) : "no telemetry file yet";
            string text =
                $"Server tick timing (F6), last {s.Ticks} ticks to tick {s.LastTick}\n" +
                $"p50 {Ms(s.P50Us)}  p99 {Ms(s.P99Us)}  worst {Ms(s.WorstUs)}\n" +
                $"over budget {s.OverBudget}  worst late start {Ms(s.WorstLateUs)}  overruns {s.Overruns}\n" +
                $"reload: server {ReloadText(reload?.LastServerResult ?? 0)} at tick {reload?.LastServerTick ?? 0}; client {reload?.Status ?? "-"}\n" +
                $"{telemetry?.Directory} / {file}";
            var rect = new Rect(Screen.width - 610f, 120f, 600f, 92f);
            GUI.Box(rect, GUIContent.none);
            GUI.Label(new Rect(rect.x + 6f, rect.y + 4f, rect.width - 12f, rect.height - 8f), text);
        }

        private static string Ms(long us) => (us / 1000.0).ToString("0.00") + " ms";

        private static string ReloadText(long result) => result switch
        {
            DevTelemetryLayout.ReloadAccepted => "accepted",
            DevTelemetryLayout.ReloadRejected => "REJECTED",
            DevTelemetryLayout.ReloadNotServer => "nothing to swap",
            _ => "none yet",
        };

        private void OnDestroy()
        {
            if (telemetry != null)
                telemetry.Received -= OnTelemetry;
            _key?.Dispose();
        }
    }
}
#endif
