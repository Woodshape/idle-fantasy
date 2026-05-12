# First Game Loop Slice Implementation Plan

## Goal

Implement the smallest observable autonomous RPG loop:

An adventurer leaves the town, hunts a nearby monster, resolves probability-based combat, collects rewards, returns to the town, and becomes ready to repeat.

This slice should validate the core simulation direction before adding shops, equipment, classes, dungeons, procedural world generation, or settlement upgrades.

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

- One town/home marker.
- One autonomous adventurer.
- One or more simple monsters.
- A small top-down arena-style test scene, not a real world map.
- Adventurer intention state machine.
- Adventurer combat state machine.
- Continuous movement.
- Tick-based combat timing.
- Probability-based combat rolls.
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
- Procedural world generation.
- Real world map or biome layout.
- Save/load.

## Design Decisions Applied

- The first slice uses a town, not a village, as the home point.
- Adventurer hiring is manual and costs player gold in the full design, but hiring UI is out of scope for this slice.
- The slice starts with one pre-existing adventurer so the loop can be validated before adding recruitment.
- Time is hybrid: movement is continuous, while combat and similar mechanics resolve on ticks.
- There is no pause assumption for now; the slice should keep running.
- Combat is random and should use explicit probability formulas followed by rolls.
- The player is abstract and has no world avatar.
- No real world map is needed yet. The slice only needs town, adventurer, and monster positions.
- Far-away adventurer simulation is irrelevant for this slice.

## Proposed Runtime Model

Use simple Godot nodes first, not a large data framework.

- `Town`: `Node2D` marker with a service radius and display name.
- `Adventurer`: `Node2D` or `CharacterBody2D` actor that owns identity, stats, inventory, position, and references to its controllers.
- `AdventurerController`: child `Node` or companion script that owns the high-level intention state machine, movement decisions, target selection requests, and return-to-town behavior.
- `AdventurerCombatController`: child `Node` or companion script that tracks detailed combat status, cooldowns, attack rolls, skills, spells, and conditions.
- `Monster`: `Node2D` or `Area2D` with stats, reward values, and alive/dead state.
- `GameController`: `Node` that owns high-level references, spawns initial actors, and provides target lookup.
- `Hud`: lightweight labels for adventurer state, gold, experience, and combat status.

Keep scripts small and direct until the loop is proven.

Important future-facing constraint: adventurer movement and targeting should be exposed through methods such as `SetMoveTarget`, `SetCombatTarget`, or `RequestAction` rather than being buried inside autonomous states only. `AdventurerController` can call those methods for AI behavior, while party leadership or a player possession spell can later drive the same adventurer through the same action interface.

## Adventurer AI State Machines

Use two distinct state machines so world decisions do not become tangled with combat details.

### Intention State Machine

This high-level state machine lives in `AdventurerController` and tracks what the adventurer is trying to do in the game world.

Initial intention states:

- `IdleInTown`: wait briefly, then look for a target.
- `ChooseTarget`: ask `GameController` for the nearest living monster appropriate for level.
- `TravelToTarget`: move toward the monster.
- `FightMonster`: hand detailed combat execution to the combat state machine until combat resolves.
- `CollectLoot`: add gold and experience from the monster.
- `ReturnToTown`: move back to the town.
- `RecoverInTown`: restore health and prepare to loop again.
- `Dead`: stop acting and emit death event.

State transitions should be explicit and logged. This will make simulation bugs easier to diagnose than burying decisions in movement code.

Reserve these states for later, but do not implement them in this slice:

- `Possessed`: player spell has temporary direct control.
- `FindParty`: adventurer seeks allies for a dangerous target.
- `InParty`: adventurer follows party-level decisions.

### Combat State Machine

This lower-level state machine lives in `AdventurerCombatController` and tracks the adventurer's detailed combat status while the intention state is `FightMonster`.

Initial combat states:

- `OutOfCombat`: no active combat target.
- `Engaging`: combat target selected, entering attack range or preparing first action.
- `Ready`: can choose the next combat action.
- `AttackCooldown`: waiting for the next basic attack tick.
- `SkillCooldown`: waiting for a skill cooldown before it can be used again.
- `Casting`: preparing a spell or ability that resolves after a cast time.
- `Recovering`: short post-action recovery window.
- `Disabled`: stunned, rooted, feared, silenced, or otherwise unable to act.
- `Defeated`: combatant has reached zero health.

The first slice only needs basic attack cooldowns, hit rolls, misses, damage, and death. Skills, spells, and conditions should be represented by the model shape but can remain stubbed until later.

## Minimal Stats

Adventurer:

- `Name`
- `Level`
- `Experience`
- `Gold`
- `Health`
- `MaxHealth`
- `Attack`
- `Accuracy`
- `Defense`
- `Evasion`
- `Speed`
- `IntentionState`
- `CombatState`
- `AttackCooldownRemaining`

Monster:

- `MonsterName`
- `Level`
- `Health`
- `MaxHealth`
- `Attack`
- `Accuracy`
- `Defense`
- `Evasion`
- `GoldReward`
- `ExperienceReward`
- `IsAlive`
- `CombatState`

Combat timing:

```text
combat_tick_interval = 1.0 seconds
```

Hit formula shape:

```text
hit_chance = clamp(base_hit_chance + attacker_accuracy - defender_evasion, min_hit_chance, max_hit_chance)
attack_hits = rng_roll_0_to_1 <= hit_chance
```

Damage formula for successful attacks:

```text
damage = max(1, attacker_attack - defender_defense)
```

Use fixed combat tick intervals at first. Randomness belongs in explicit probability rolls, not in hidden formula side effects.

## TestBridge Events

Emit these domain events when the bridge is active:

- `adventurer_state_changed`
- `adventurer_target_selected`
- `adventurer_arrived_at_target`
- `combat_started`
- `combat_state_changed`
- `attack_roll_resolved`
- `damage_applied`
- `monster_defeated`
- `loot_collected`
- `adventurer_returned_to_town`
- `adventurer_died`

Publish named states:

- `adventurer`
- `monster`
- `game_loop`

## Implementation Steps

1. Create a simple game scene for the slice.
   - Add a root `Node2D`.
   - Add a town marker.
   - Add one adventurer.
   - Add one monster.
   - Add lightweight labels for state visibility.

2. Add `Monster.cs`.
   - Store basic stats.
   - Expose `ApplyDamage`.
   - Emit or provide death/reward data.

3. Add `Town.cs`.
   - Store service radius and recovery behavior.
   - Provide a known return position.

4. Add `Adventurer.cs`.
   - Store identity, stats, gold, experience, and current target references.
   - Own or expose controller references.
   - Provide shared action methods such as `SetMoveTarget`, `SetCombatTarget`, and `ApplyDamage`.
   - Publish shared state through TestBridge.

5. Add `AdventurerController.cs`.
   - Implement the high-level intention state machine.
   - Implement movement toward a target position.
   - Request hunt targets from `GameController`.
   - Delegate detailed combat behavior to `AdventurerCombatController`.
   - Emit bridge events and publish state.

6. Add `AdventurerCombatController.cs`.
   - Track combat state.
   - Track attack cooldowns.
   - Resolve hit chance rolls.
   - Apply damage on successful attacks.
   - Leave skills, spells, and conditions as explicit future extension points.

7. Add `GameController.cs`.
   - Keep references to town, adventurer, and monsters.
   - Provide `FindHuntTarget`.
   - Detect when the loop has completed at least once.

8. Wire the scene as the main scene or add a dedicated test scene.
   - Prefer making the slice the main scene while the project is still early.
   - Keep `TestBridge` autoload unchanged.

9. Build the C# project.
   - Build both existing project files as required by the project workflow.

10. Run a live verification.
   - Launch via the project-specific Godot command.
   - Confirm bridge startup.
   - Observe state/event output until one loop completes.

## Acceptance Criteria

- The game starts into a visible top-down slice scene.
- The scene contains a town marker, one adventurer, and at least one monster.
- The adventurer autonomously leaves the town without player input.
- The adventurer reaches a monster.
- Combat starts on tick intervals.
- The adventurer has a high-level intention state and a separate detailed combat state.
- Each attack computes a hit chance and rolls against it.
- Combat produces visible/logged hits, misses, and damage.
- If the adventurer wins, the monster dies and rewards are added.
- The adventurer returns to the town.
- The adventurer recovers and can begin another hunt.
- If the adventurer dies, the state becomes `Dead` and the loop stops for that adventurer.
- TestBridge emits enough events/state to verify the loop without relying only on visuals.

## Risks And Constraints

- Avoid overbuilding the architecture before the loop is fun to watch.
- Keep the boundary clear: intention state decides goals, combat state resolves moment-to-moment combat.
- Keep combat formulas explicit and log the rolled values so failures are diagnosable.
- Do not introduce inventory, shops, or procedural generation in this slice.
- Do not build a real world map yet; simple positioned nodes are enough.
- Do not add hiring UI yet; start with one pre-placed adventurer.
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
- Add the no-money downtrodden adventurer fallback.
- Add a shop and potion resupply.
- Add monster respawning and danger scaling.
- Add procedural arena/world generation.
- Add a basic dungeon entrance as a higher-risk target.
- Add adventurer leveling and stat growth.
