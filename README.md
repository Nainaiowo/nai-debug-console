# Nai Debug Console

Nai Debug Console is a local Dalamud plugin for capturing combat-debug data to JSONL files and inspecting Strategy Board/UI internals.

It records structured log-message events, action-effect events, raw packet events, optional party HP/status snapshots, and broad pull-recorder snapshots so the data can be compared against Better Deaths behavior.

## Commands

```text
/ndc
```

## Output

Logs are written to the plugin config folder under `logs`.

Each line is one JSON object. Send the generated `.jsonl` file when we need to audit structured log streams, action effects, or UI/Strategy Board behavior.

Use `Save current capture` after a test run when the session may have rotated across multiple log files. The saved bundle combines every file from the current capture session into one `records.jsonl` with metadata in `capture-info.json`.

## Tools

- Combat JSONL logger
- ActionEffect capture
- EffectResult packet capture
- ActorControl packet capture
- Party HP/status snapshots
- Full pull recorder with object-table, status, position, HP/shield, condition, and addon-lifecycle snapshots
- Addon lifecycle event list
- Addon node and AtkValue snapshots
- Strategy Board shared/saved folder, board, and object inspector
- Strategy Board test-board creation
- Tofu function watcher for Strategy Board share/save/notification research
- Strategy share trace mode for focused manual share testing

## Strategy Share Trace

Use this when researching how the native Strategy Board share flow works.

1. Open `/ndc`.
2. Go to `Share Trace`.
3. Click `Start share trace`.
4. Manually open Strategy Board, select the board or folder, click Share, then confirm Yes.
5. Click `Stop share trace`.
6. Click `Save trace bundle`.

The trace records addon lifecycle events, Tofu function calls, board/list selection-state changes, a start/end Strategy Board snapshot, and an automatic snapshot of the confirmation dialog when it appears. The UI shows all captured trace rows by default and can narrow the view with text search or focused-only filtering. Treat the output as research evidence, not as confirmed implementation truth until the trace is compared against the in-game behavior.

Saved trace bundles are written to the plugin config folder under `logs/share-traces`.

## Full Pull Recorder

Use this when researching fight mechanics that may come from statuses, action effects, actor-control packets, or visible world state.

1. Open `/ndc`.
2. Go to `Logger`.
3. Keep `Capture enabled` on.
4. Enable `Record everything visible while enabled`.
5. Leave `Include full object table snapshots`, `Capture EffectResult packets`, and `Capture ActorControl packets` on unless the log becomes too large.
6. Run the pull, then turn the recorder off.
7. Click `Save current capture`.
8. Open the capture folder and use the newest capture bundle.

The recorder starts a fresh JSONL file when enabled. It records broad snapshots while the normal capture gate is open, so the `Capture only while bound by duty` setting still applies.
