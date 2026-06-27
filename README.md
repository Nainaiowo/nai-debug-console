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
