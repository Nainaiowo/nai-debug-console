# Nai Debug Console

Nai Debug Console is a local Dalamud plugin for capturing combat-debug data to JSONL files and inspecting Strategy Board/UI internals.

It records structured log-message events, action-effect events, and optional party HP/status snapshots so the data can be compared against Better Deaths behavior.

## Commands

```text
/ndc
```

## Output

Logs are written to the plugin config folder under `logs`.

Each line is one JSON object. Send the generated `.jsonl` file when we need to audit structured log streams, action effects, or UI/Strategy Board behavior.

## Tools

- Combat JSONL logger
- ActionEffect capture
- Party HP/status snapshots
- Addon lifecycle event list
- Addon node and AtkValue snapshots
- Strategy Board shared/saved list inspector
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

The trace records focused addon lifecycle events, Tofu function calls, board/folder hover probe changes, a start/end Strategy Board snapshot, and an automatic snapshot of the confirmation dialog when it appears. Treat the output as research evidence, not as confirmed implementation truth until the trace is compared against the in-game behavior.

Saved trace bundles are written to the plugin config folder under `logs/share-traces`.
