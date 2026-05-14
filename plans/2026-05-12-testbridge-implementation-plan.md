# TestBridge Implementation Plan

## Goal

Add a reusable `TestBridge` autoload that lets a headless or windowed Godot run accept live commands from a file and emit structured JSONL events and state back to a file, so an agent can drive and observe a running scene without relaunching.

## Architecture

1. Add an autoload singleton `TestBridge`.
2. On startup, `TestBridge` reads bridge config from CLI user args.
3. It opens a session directory or file prefix under `/tmp` or another writable path.
4. Every frame or on a polling timer, it reads new command lines from a command JSONL file.
5. It parses each command, dispatches it into the live scene, and emits JSONL events to an event file.
6. Gameplay code can publish higher-level domain events to `TestBridge` without knowing anything about file I/O.

## Recommended Files

- `TestBridge.cs`
- `TestCommand.cs` or small internal command structs inside `TestBridge.cs`
- `TestEvent.cs` or helper serializer methods
- `project.godot` autoload registration
- Later optional helpers:
  - `ITestBridgePublisher.cs`
  - `TestBridgeCombatAdapter.cs`
  - `TestBridgePlayerAdapter.cs`

## Session Model

Use a directory-based session, not just two flat files. Example:

- `/tmp/idle-fantasy-test/session-001/commands.jsonl`
- `/tmp/idle-fantasy-test/session-001/events.jsonl`
- `/tmp/idle-fantasy-test/session-001/state.json`
- `/tmp/idle-fantasy-test/session-001/meta.json`

Why:

- easier cleanup
- supports multiple runs
- easy to inspect manually
- room for snapshots and debug artifacts

## CLI Contract

Support one required user arg:

- `--test-bridge-dir=/tmp/idle-fantasy-test/session-001`

Optional args:

- `--test-bridge-poll-ms=50`
- `--test-bridge-quit-when-idle`
- `--test-bridge-verbose`
- `--test-bridge-scene-tag=click_move_test`

`TestBridge` should remain dormant if no bridge arg is provided.

## Command Transport

Use append-only JSONL for commands.

Rules:

- one JSON object per line
- each command has a monotonically increasing `id`
- bridge tracks last processed byte offset or command id
- ignore malformed lines but emit an error event
- process commands in order

Initial command set:

- `ping`
- `quit`
- `snapshot`
- `click_world`
- `click_viewport`
- `click_node`
- `wait_for_event`
- `wait_for_state`
- `set_time_scale`

Example:

```json
{"id":1,"cmd":"click_world","x":120,"y":880}
{"id":2,"cmd":"wait_for_event","event":"player_arrived","timeout_ms":5000}
{"id":3,"cmd":"click_node","path":"root/Enemies/Goblin"}
```

## Event Transport

Use append-only JSONL for events.

Every event should include:

- `ts`
- `frame`
- `type`
- `source`
- optional `cmd_id`
- payload fields

Core system events:

- `bridge_started`
- `bridge_error`
- `command_received`
- `command_completed`
- `command_failed`
- `snapshot`
- `bridge_stopped`

Gameplay events:

- `player_target_set`
- `player_moving`
- `player_arrived`
- `combat_started`
- `damage_applied`
- `combat_ended`

Example:

```json
{"ts":1715520000.25,"frame":42,"type":"player_arrived","source":"PlayerController","position":[120.0,880.0],"cmd_id":2}
```

## Command Dispatch Strategy

Split commands into two groups.

Immediate bridge commands:

- `ping`
- `quit`
- `snapshot`
- `set_time_scale`

Gameplay interaction commands:

- `click_world`
- `click_viewport`
- `click_node`

For gameplay commands, do not hardcode player-specific behavior into `TestBridge`. Instead:

- `TestBridge` should synthesize generic input where possible
- domain code should publish events back
- optional adapters can expose richer domain actions later

That keeps the bridge general.

## Input Injection Plan

Support three levels:

1. `click_viewport`
   - synthesize `InputEventMouseButton` with viewport coordinates
   - closest to real user input
   - best default for UI and click-to-move
2. `click_world`
   - convert world position to viewport position through current camera or canvas transform
   - then inject as viewport click
3. `click_node`
   - resolve node path
   - derive clickable position from node type or global transform
   - convert to viewport click and inject

This avoids bespoke "move player here" APIs and keeps the bridge user-input-centric.

## Observation Plan

Implement three observation layers.

1. Bridge-level snapshots
   - scene name
   - frame
   - time scale
   - known camera
   - selected node states if requested
2. Generic node state extraction
   - for `Node2D`: global position, rotation, visible
   - for `CharacterBody2D`: velocity
   - for `Area2D` or enemies: alive or disabled if discoverable
3. Domain event publishing
   - `PlayerController` emits `player_target_set` and `player_arrived`
   - future combat code emits combat events

This gives both generic and game-specific visibility.

## Public API for Gameplay Code

Expose a tiny API on the autoload:

- `IsActive`
- `EmitEvent(type, payload)`
- `EmitState(name, payload)`
- `TryGetActiveCommandId()`

Gameplay scripts should call this when meaningful things happen. Example:

- player sets target
- player reaches destination
- attack starts
- damage lands
- enemy dies

## Implementation Phases

1. Bridge skeleton
   - create `TestBridge.cs`
   - parse CLI args
   - open session files
   - emit `bridge_started`
2. JSONL I/O
   - append event writer
   - command polling with offset tracking
   - malformed-command handling
3. Core commands
   - `ping`, `quit`, `snapshot`, `set_time_scale`
4. Input commands
   - `click_viewport`
   - `click_world`
   - `click_node`
5. Generic observation
   - scene and frame metadata
   - node snapshot command
6. Gameplay integration
   - wire `PlayerController` to emit `player_target_set` and `player_arrived`
   - remove the current ad hoc startup test args once bridge path replaces them
7. Wait semantics
   - implement `wait_for_event`
   - implement `wait_for_state`
   - include timeout handling
8. Verification scenes
   - add a minimal click-move test scenario
   - verify full loop in headless mode

## Verification Plan

1. Launch headless with a fresh bridge dir.
2. Confirm `bridge_started` appears in `events.jsonl`.
3. Append `ping`; confirm `command_completed`.
4. Append `snapshot`; confirm state event.
5. Append `click_world`; confirm `player_target_set`.
6. Wait until `player_arrived`.
7. Append second click; confirm another arrival.
8. Append `quit`; confirm clean shutdown.

Later combat verification:

- click enemy
- assert `combat_started`
- observe damage events
- assert `combat_ended`

## Risks

- Coordinate transforms may differ between headless and windowed runs.
- File polling must avoid rereading partial lines.
- Commands that wait internally can block the bridge if implemented naively.
- Too much per-frame logging will bloat event files quickly.
- Domain events need explicit instrumentation; generic snapshots alone will not explain combat well.

## Design Constraints

- Keep the bridge dormant unless explicitly enabled by CLI arg.
- Keep the bridge append-only and inspectable.
- Prefer generic click injection over game-specific action commands.
- Add domain-specific events only where generic observation is insufficient.
- Do not couple the bridge to one scene or one controller.

## First Deliverable

The first useful version should support:

- autoload registration
- `--test-bridge-dir=...`
- `ping`
- `snapshot`
- `click_world`
- `quit`
- JSONL events
- `PlayerController` event emission for target set and arrival

That is enough to prove a live interactive headless loop without overbuilding it.
