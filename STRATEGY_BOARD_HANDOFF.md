# Strategy Board Handoff

Date: 2026-06-27

This file records where the Strategy Board / Tofu share research stopped before switching back to Better Deaths work.

## Current State

- Nai Debug Console `v0.1.0.7` is pushed.
- Source commit: `dcc99c1 Improve strategy board share debugging`.
- Feed commit: `91dd9f6 Update Nai Debug Console feed`.
- Release: `https://github.com/Nainaiowo/nai-debug-console/releases/tag/v0.1.0.7`.
- Local follow-up removed the temporary `Create 31-char text board` test helper and button after the test was done. Keep that helper out unless deliberately re-testing text limits.

## Confirmed

- Strategy Board data is exposed through `TofuModule.Instance()`.
- Saved and shared board lists can be read.
- Board objects can be read, including type, position, scale, angle, RGBA bytes, visibility, locked flag, raw flags, args, and text.
- New saved Strategy Boards can be created through the game's `TofuModule.CreateBoard` path.
- New folders can be created.
- Boards can be placed/appended into folders.
- Board contents can include text objects and icon/object markers.
- Text objects safely hold 30 visible characters.
- A 31-character test board could be created, but the saved snapshot came back with 30 visible characters. Treat the practical text chunk cap as 30.
- The share confirmation popup can be captured from `SelectYesno`.
- The folder name can be parsed from `SelectYesno AtkValue[0]`.
- Latest confirmed prompt parse:
  - Trace file: `%APPDATA%\XIVLauncher\pluginConfigs\NaiDebugConsole\logs\share-traces\strategy-share-trace-20260627-141724.json`
  - Parsed folder: `FOLDER_TEST`
  - Raw prompt: `Are you sure you wish to share the strategy board folder "FOLDER_TEST" with your party?`
- `AgentTofuList.ContextMenuOptions` was observed around the share flow:
  - `code 10` opens the context submenu.
  - `code 21` opens the share confirmation.

## Not Confirmed

- Automatic folder sharing is not solved.
- The latest trace did not capture the post-Yes send path.
- In that trace, there were no rows for:
  - `TofuBoardOverview.WriteToUnpackedBoard`
  - `TofuHelper.HandleTofuConfirmationPacket`
  - `SelectYesno PostHide`
- That means the trace reached the confirmation prompt and parsed it correctly, but did not prove the final "Yes accepted, boards packed, boards sent" step.

## Important Caution

- Do not hand-edit local FFXIV `.dat` files.
- Do not craft/send Strategy Board packets unless the UI callback path is proven impossible and the packet shape is fully understood.
- Prefer the game's own Tofu/UI paths over writing local files or inventing packets.
- Keep object values conservative:
  - Known `TofuObjectType` only.
  - Text chunks capped at 30 characters.
  - No line breaks in text objects.
  - Real observed coordinates.
  - Scale `100` unless deliberately testing a known observed value.
  - Angle `0` unless deliberately testing rotation.
  - Default args `0,0,0` unless an object has verified real args.

## Best Next Research Path

1. Improve Tofu hook logs so they print real packet/session fields, not just pointer addresses.
2. Focus especially on:
   - `TofuStartSharingPacket`
   - `TofuConfirmationPacket`
   - `TofuShareSession`
   - `TofuPackedBoard`
   - `TofuPackedBoardShare`
   - `TofuBoardOverview.WriteToUnpackedBoard`
3. Run a clean trace where the user:
   - Starts trace.
   - Opens Strategy Board.
   - Selects the test folder.
   - Chooses Share.
   - Clicks Yes while the trace is still running.
   - Waits 1-2 seconds.
   - Stops and saves trace.
4. If the post-Yes path still does not show up, investigate UI callback replay:
   - Reproduce the same `AgentTofuList.ContextMenuOptions` sequence safely.
   - First goal: open the confirmation prompt programmatically.
   - Later goal: confirm through the same game UI path.

## Current Working Theory

The cleanest eventual implementation is not packet crafting. It is likely:

1. Build the Strategy Board payload locally through `TofuModule`.
2. Put boards into a folder.
3. Use the game's own Strategy Board agent/UI callback path to share that folder.
4. Let the game serialize, pack, and send the folder normally.
