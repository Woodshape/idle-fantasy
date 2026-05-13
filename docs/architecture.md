# Idle Fantasy Architecture Notes

## Frame Loop Boundaries

Idle Fantasy uses three different update scopes. Gameplay code should be placed in the narrowest scope that matches its timing needs.

## `_Process`

Use `_Process` for rendered-frame work that is not gameplay-authoritative.

Appropriate work:

- UI and HUD refresh.
- Visual-only timers.
- Non-authoritative presentation updates.
- Test bridge polling and command waiting.
- Timers where exact physics-step consistency is not important.

Current examples:

- `GameController._Process` updates the monster wave respawn timer, HUD text, and bridge-visible game state.
- `TestBridge._Process` polls commands and resolves bridge waits when the test bridge is active.

## `_PhysicsProcess`

Use `_PhysicsProcess` for movement, physics-adjacent interpolation, state machines that depend on smooth world updates, and the fixed simulation tick accumulator.

Appropriate work:

- Movement toward a target.
- Autonomous intention state machines.
- Fixed-step accumulator logic.
- Position-sensitive behavior that should be consistent across machines.

Current examples:

- `GameController._PhysicsProcess` accumulates time and emits a simulation tick every `SimulationTickInterval`.
- `AdventurerController._PhysicsProcess` runs the adventurer intention state machine and movement.
- `Monster._PhysicsProcess` moves monsters toward their aggro target.
- `PlayerController._PhysicsProcess` moves the click-controlled player node.

## Simulation Ticks

Use simulation ticks for authoritative gameplay checks and combat math that do not need to run every frame.

Appropriate work:

- Combat action cooldowns, casts, recovery, and resolution.
- Monster proximity aggro scans.
- Monster encounter join retries.
- Other periodic AI or world checks where tick frequency is sufficient.

Current examples:

- `GameController._PhysicsProcess` advances `_simulationTickCount` when the accumulator reaches `SimulationTickInterval`.
- Each tick calls `Monster.ProcessSimulationTick` for proximity aggro and aggro encounter joins.
- Each tick calls `AdventurerCombatController.ProcessSimulationTick` for active combat encounters.

## Aggro Placement

Proximity aggro must not scan every physics frame. It is currently tick-based:

1. `GameController` advances the simulation tick.
2. Each monster receives `ProcessSimulationTick(game, tick)`.
3. The monster checks proximity aggro only if it is alive and has no living aggro target.
4. A monster with an existing living aggro target retries joining the target's active encounter once per tick.

Monster aggro movement remains in `_PhysicsProcess` because it is continuous movement, not a broad gameplay scan.

## Rule Of Thumb

If the code asks "should this gameplay condition become true now?", prefer the simulation tick unless the answer must depend on precise per-frame movement.

If the code asks "where should this body be this frame?", use `_PhysicsProcess`.

If the code asks "what should the player or test bridge see right now?", use `_Process`.
