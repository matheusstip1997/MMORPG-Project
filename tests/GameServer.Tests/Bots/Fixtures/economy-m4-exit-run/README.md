# A recorded m4-exit run (WP-M4-15)

The run the economy dashboard comparison (`Economy/EconomyDashboardRunTests`) and the report's M4 counters
(`Bots/EconomyReportM4RunTests`) read. Recorded on 2026-11-15 with the WP-M4-15 branch on its own databases:

```
dotnet run --project src/Tools/DbMigrate -- --dev --db-suffix wpm415
powershell -ExecutionPolicy Bypass -File tools/load/Run-M4Exit.ps1 -DbSuffix wpm415 -NoDocker -RunId wpm415-m4-exit-1
```

Result: 65 entries, 63 done, 2 not yet (the contract entries, WP-M4-10), 0 failed; tick p99 0.46 ms over 38,846 ticks.

- `telemetry/m4-exit.jsonl`: the run's telemetry file (`artifacts/load/wpm415-m4-exit-1/telemetry`), its header line and the
  records of the kinds the economy report reads (gold, items, exp, the M3 and M4 kinds), the rest (ticks, counters, combat)
  cut.
- `ledger.json`: every `gold_ledger` row of the run's realm database (faucets, sinks and moves: `atMs`, `delta`, `flow`,
  `reason`) and every mail expiry (`mail_ops` kind 3 with its mail's `kind` and the `gold`; none in this run), exported once
  read-only:

```
docker exec mmorpg-postgres-1 psql -U mmorpg -d mmorpg_realm_1_wpm415 -At -c "SELECT json_build_object('gold', (SELECT coalesce(json_agg(json_build_object('atMs', (extract(epoch FROM created_at) * 1000)::bigint, 'delta', delta, 'flow', flow, 'reason', reason) ORDER BY created_at, ledger_id), '[]') FROM gold_ledger), 'mailExpired', (SELECT coalesce(json_agg(json_build_object('atMs', (extract(epoch FROM o.created_at) * 1000)::bigint, 'kind', m.kind, 'gold', o.gold) ORDER BY o.created_at), '[]') FROM mail_ops o JOIN mail m ON m.mail_id = o.mail_id WHERE o.kind = 3))"
```

A new recording replaces both files together (and the figures in `EconomyReportM4RunTests`).
