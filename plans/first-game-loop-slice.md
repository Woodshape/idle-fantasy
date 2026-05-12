# First Game Loop Slice Implementation Plan

## Goal

Implement the smallest observable autonomous RPG loop:

An adventurer leaves the village, hunts a nearby monster, resolves combat, collects rewards, returns to the village, and becomes ready to repeat.

This slice should validate the core simulation direction before adding shops, equipment, classes, dungeons, or settlement upgrades.

## Current Starting Point

The project currently has:

- Godot 4.6 C# project files.
- A main scene configured in `project.godot`.
- `PlayerController.cs` with click-to-move behavior.
- `TestBridge` autoload support for command/event/state observation.
- A TestBridge implementation plan in `plans/testbridge-implementation-plan.md`.

The first slice should reuse the existing bridge event/state pattern so autonomous behavior can be verified headlessly later.

## Slice Scope

Included:

- One village/home marker.
- One autonomous adventurer.
- One or more simple monsters.
- A small top-down test map.
- Adventurer finite state machine.
- Basic travel, combat, rewards, death, and return-to-town.
- Runtime-visible state through labels, console logs, and TestBridge events.

Excluded for now:

- Hiring UI.
- Multiple settlements.
- Classes.
- Equipment.
- Shops.
- Potions.
- Player spells.
- Direct possession/control of adventurers.
- Parties.
- Dungeons.
- Procedural generation.
- Save/load.

## Proposed Runtime Model

Use simple Godot nodes first, not a large data framework.

- `Village`: `Node2D` marker with a service radius and display name.
- `Adventurer`: `Node2D` or `CharacterBody2D` with stats, movement, inventory, and state machine.
- `Monster`: `Node2D` or `Area2D` with stats, reward values, and alive/dead state.
- `WorldController`: `Node` that owns high-level references, spawns initial actors, and provides target lookup.
- `Hud`: lightweight labels for adventurer state, gold, experience, and combat status.

Keep scripts small and direct until the loop is proven.

Important future-facing constraint: adventurer movement and targeting should be exposed through methods such as `SetMoveTarget`, `SetCombatTarget`, or `RequestAction` rather than being buried inside autonomous states only. That keeps the same adventurer usable later by AI, party leadership, or a player possession spell.

## Adventurer State Machine

Initial states:

- `IdleInVillage`: wait briefly, then look for a target.
- `ChooseTarget`: ask `WorldController` for the nearest living monster appropriate for level.
- `TravelToTarget`: move toward the monster.
- `FightMonster`: exchange attacks until someone dies.
- `CollectLoot`: add gold and experience from the monster.
- `ReturnToVillage`: move back to the village.
- `RecoverInVillage`: restore health and prepare to loop again.
- `Dead`: stop acting and emit death event.

State transitions should be explicit and logged. This will make simulation bugs easier to diagnose than burying decisions in movement code.

Reserve these states for later, but do not implement them in this slice:

- `Possessed`: player spell has temporary direct control.
- `FindParty`: adventurer seeks allies for a dangerous target.
- `InParty`: adventurer follows party-level decisions.

## Minimal Stats

Adventurer:

- `Name`
- `Level`
- `Experience`
- `Gold`
- `Health`
- `MaxHealth`
- `Attack`
- `Defense`
- `Speed`
- `State`

Monster:

- `MonsterName`
- `Level`
- `Health`
- `MaxHealth`
- `Attack`
- `Defense`
- `GoldReward`
- `ExperienceReward`
- `IsAlive`

Damage formula:

```text
damage = max(1, attacker_attack - defender_defense)
```

Use fixed combat intervals at first. Add randomness later only after deterministic behavior is easy to test.

## TestBridge Events

Emit these domain events when the bridge is active:

- `adventurer_state_changed`
- `adventurer_target_selected`
- `adventurer_arrived_at_target`
- `combat_started`
- `damage_applied`
- `monster_defeated`
- `loot_collected`
- `adventurer_returned_to_village`
- `adventurer_died`

Publish named states:

- `adventurer`
- `monster`
- `world_loop`

## Implementation Steps

1. Create a simple game scene for the slice.
   - Add a root `Node2D`.
   - Add a village marker.
   - Add one adventurer.
   - Add one monster.
   - Add lightweight labels for state visibility.

2. Add `Monster.cs`.
   - Store basic stats.
   - Expose `ApplyDamage`.
   - Emit or provide death/reward data.

3. Add `Village.cs`.
   - Store service radius and recovery behavior.
   - Provide a known return position.

4. Add `Adventurer.cs`.
   - Implement the finite state machine.
   - Implement movement toward a target position.
   - Implement deterministic combat timing.
   - Track gold and experience.
   - Emit bridge events and publish state.

5. Add `WorldController.cs`.
   - Keep references to village, adventurer, and monsters.
   - Provide `FindHuntTarget`.
   - Detect when the loop has completed at least once.

6. Wire the scene as the main scene or add a dedicated test scene.
   - Prefer making the slice the main scene while the project is still early.
   - Keep `TestBridge` autoload unchanged.

7. Build the C# project.
   - Build both existing project files as required by the project workflow.

8. Run a live verification.
   - Launch via the project-specific Godot command.
   - Confirm bridge startup.
   - Observe state/event output until one loop completes.

## Acceptance Criteria

- The game starts into a visible top-down slice scene.
- The adventurer autonomously leaves the village without player input.
- The adventurer reaches a monster.
- Combat starts and produces visible/logged damage.
- If the adventurer wins, the monster dies and rewards are added.
- The adventurer returns to the village.
- The adventurer recovers and can begin another hunt.
- If the adventurer dies, the state becomes `Dead` and the loop stops for that adventurer.
- TestBridge emits enough events/state to verify the loop without relying only on visuals.

## Risks And Constraints

- Avoid overbuilding the architecture before the loop is fun to watch.
- Keep combat deterministic initially so failures are reproducible.
- Do not introduce inventory, shops, or procedural generation in this slice.
- The current `PlayerController` may become obsolete or should be treated as an input/movement prototype.
- Avoid coupling click-to-move directly to the old `PlayerController`; possession should eventually drive adventurer actions through the same action interface used by AI.
- Party logic should not be added until the solo loop has clean target selection, rewards, and return behavior.
- The scene structure should stay simple enough to inspect manually in Godot.

## Next Slice Candidates

After the first loop works, choose one:

- Add multiple adventurers with independent decisions.
- Add parties with shared targeting, combat, and split rewards.
- Add a player spell bar and a possession spell prototype.
- Add a tavern hiring flow.
- Add a shop and potion resupply.
- Add monster respawning and danger scaling.
- Add a basic dungeon entrance as a higher-risk target.
- Add adventurer leveling and stat growth.
