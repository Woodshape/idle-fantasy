# Combat Main Loop Plan

## Goal

Refactor combat from a single paired attack tick into an entity-driven combat loop where each combatant owns its own action timing.

The next combat slice should make adventurer and monster attacks independent from the game simulation tick. The game tick is for lower-frequency decisions and maintenance work such as AI reconsideration, buff and debuff duration updates, regeneration pulses, periodic threat checks, and other cost-controlled simulation updates.

Combat actions should run from per-entity timers and action state, so a fast adventurer can attack at a different cadence from a slow monster, casts can complete independently, and skill cooldowns can mature while other actors are acting.

## Current Starting Point

The current first loop has:

- `AdventurerController` handling high-level intention state.
- `AdventurerCombatController` handling combat for the adventurer.
- `Monster` storing stats, health, rewards, and a combat state.
- Basic attacks resolved inside `AdventurerCombatController.ResolveCombatTick`.
- A fixed `CombatTickInterval = 1.0`.
- The adventurer attacks first, and the monster immediately attacks back if alive.
- Skill, casting, recovering, and disabled states exist only as enum values.

This is acceptable for the first slice, but it is not the desired combat model.

## Core Design Decision

Separate these two clocks:

- **Simulation tick**: low-frequency world and decision updates.
- **Combat action timers**: per-entity timers that drive attacks, skills, casts, recovery, and cooldowns.

The simulation tick should not decide exactly when a sword swing lands. It should be used for work that can tolerate batching, such as:

- Reconsidering AI goals.
- Updating buff and debuff durations.
- Applying regeneration or damage-over-time pulses.
- Checking long-running effects.
- Refreshing target selection when no target exists.
- Publishing lower-frequency aggregate state.

Combat action timers should be updated continuously through frame delta or physics delta while the combatant is in combat.

## Target Runtime Model

Introduce a shared combat model that works for adventurers and monsters.

- `Combatant`: shared interface or base component for anything that can fight.
- `CombatStats`: attack, accuracy, defense, evasion, attack speed, cast speed, cooldown modifiers, max health, current health.
- `CombatAction`: data describing one usable action.
- `CombatActionRunner`: executes the active action state for one combatant.
- `CombatEncounter`: owns active combatants, target relationships, and end conditions.
- `CombatDecisionController`: chooses the next action when a combatant is ready.

Keep the initial implementation simple. This can start as C# classes and components attached to the existing `Adventurer` and `Monster` nodes rather than a large ECS or data framework.

## Combatant State

Each combatant should own these runtime fields:

- `CombatState`
- `CurrentTarget`
- `QueuedAction`
- `ActiveAction`
- `GlobalCooldownRemaining`
- `BasicAttackCooldownRemaining`
- `CastRemaining`
- `RecoveryRemaining`
- `SkillCooldowns`
- `Buffs`
- `Debuffs`
- `IsAlive`
- `IsDisabled`
- `CanAct`

Initial combat states:

- `OutOfCombat`: no active encounter.
- `Engaging`: entering combat and validating target/range.
- `Ready`: can evaluate and choose an available action, or wait if no action is currently available.
- `UsingAction`: resolving an instant action.
- `Casting`: cast time is counting down.
- `Recovering`: short post-action lockout.
- `Disabled`: unable to act because of a condition.
- `Defeated`: health is zero.

Cooldowns should not be combat states. `BasicAttackCooldownRemaining`, `SkillCooldowns`, and `GlobalCooldownRemaining` are action availability timers. A combatant can remain `Ready` while one skill has 60 seconds left on cooldown, choose another available skill, make a basic attack if ready, or simply wait for the next relevant timer to mature.

The important change is that these states belong to each combatant, not only to the adventurer.

## Action Timing Model

Actions should be driven by independent timers:

- Basic attack cooldown: derived from the attacker’s attack speed; it gates only the next basic attack.
- Skill cooldown: per skill, starts when the skill is used; it gates only that skill.
- Cast time: per action, counts down after an action begins.
- Recovery time: optional post-action delay before another action can start.
- Global cooldown: optional shared lockout for most active abilities.

Cooldown timers are checked by the decision controller when selecting an action. They should not force `CombatState` to become `AttackCooldown` or `SkillCooldown`.

Initial formula:

```text
basic_attack_interval = 1.0 / attacks_per_second
```

Example stats:

```text
adventurer_attacks_per_second = 1.0
monster_attacks_per_second = 0.65
```

This means the adventurer and monster do not exchange paired turns. Each attacks when its own timer is ready.

## Basic Attack Flow

When a combatant enters combat:

1. Set `CurrentTarget`.
2. Enter `Engaging`.
3. Validate target is alive and in range.
4. Enter `Ready`.
5. Decision controller chooses an action.
6. If basic attack is chosen and its cooldown is ready, resolve attack.
7. Start basic attack cooldown based on attack speed.
8. Enter `Recovering` if recovery exists, otherwise return to `Ready`.

On every physics update:

1. Reduce combat action timers by `delta`.
2. Reduce cast and recovery timers by `delta`.
3. Reduce skill cooldown timers by `delta`.
4. If a cast completes, resolve the action.
5. If the combatant is `Ready`, ask for the next available action.
6. If the target died, clear action state and let encounter/intention logic resolve the outcome.

If no action is currently available because all relevant cooldowns are still running, the combatant stays `Ready` and waits. The next decision pass can run when a cooldown-ready event fires, on the next physics frame, or through a lightweight scheduler.

## Skill And Spell Flow

Represent skills and spells with the same `CombatAction` shape.

Suggested fields:

- `ActionId`
- `DisplayName`
- `ActionKind`: `BasicAttack`, `Skill`, `Spell`
- `Range`
- `Cooldown`
- `CastTime`
- `RecoveryTime`
- `RequiresTarget`
- `CanUseWhileMoving`
- `HitFormula`
- `DamageFormula`
- `Effects`

Initial examples:

- `basic_attack`: instant, uses attack speed cooldown.
- `heavy_strike`: instant, 4 second cooldown, higher damage.
- `spark`: 1.2 second cast time, 3 second cooldown, magic damage.

The first implementation does not need a large skill library. One basic attack plus one simple cooldown skill is enough to prove the model.

## Game Tick Responsibilities

Add a separate simulation tick service or timer.

Initial tick interval:

```text
simulation_tick_interval = 0.25 seconds
```

The simulation tick may do:

- AI target reconsideration when out of combat.
- Low-frequency combat decision hints.
- Buff and debuff duration expiry checks.
- Regeneration pulses.
- Damage-over-time or healing-over-time pulses.
- Encounter timeout checks.
- Bridge state aggregation.

The simulation tick should not:

- Trigger basic attack swings directly.
- Pair adventurer and monster attacks.
- Block cast completion until the next tick.
- Delay skill cooldown readiness beyond the timer reaching zero.

## Combat Decision Rules

Start with deterministic priority rules before adding more advanced AI.

Adventurer decision priority:

1. If dead, stop.
2. If disabled, wait.
3. If no valid target, exit combat.
4. If a high-priority skill is ready and conditions match, use it.
5. If basic attack is ready, basic attack.
6. Otherwise wait until the next action timer matures.

Monster decision priority:

1. If dead, stop.
2. If disabled, wait.
3. If no valid target, exit combat.
4. If basic attack is ready, basic attack.
5. Otherwise wait.

This keeps the first independent-timer slice understandable.

## TestBridge Events

Keep existing events and add timing-specific events.

Existing events to preserve:

- `combat_started`
- `combat_state_changed`
- `attack_roll_resolved`
- `damage_applied`
- `monster_defeated`
- `adventurer_died`

New events:

- `combatant_ready`
- `combat_action_selected`
- `combat_action_started`
- `combat_action_resolved`
- `combat_action_cooldown_started`
- `combat_action_cooldown_ready`
- `combat_cast_started`
- `combat_cast_completed`
- `combat_recovery_started`
- `combat_recovery_completed`
- `simulation_tick`
- `effect_applied`
- `effect_expired`
- `regeneration_applied`

Named states:

- `adventurer`
- `monster`
- `combat_encounter`
- `simulation_clock`

State should expose each combatant’s independent timers so tests can verify that combat is no longer turn-paired.

## Implementation Steps

1. Add shared combat action types.
   - Create `CombatAction.cs`.
   - Create `CombatActionKind`.
   - Add basic attack action construction.

2. Add shared combatant contract.
   - Create `ICombatant` or `CombatantComponent`.
   - Expose health, stats, target, and `ApplyDamage`.
   - Adapt `Adventurer` and `Monster` to this contract.

3. Add per-combatant action runner.
   - Move timer handling out of `AdventurerCombatController`.
   - Track basic attack cooldown, skill cooldowns, cast time, recovery time, and disabled state per combatant.
   - Use physics delta for action timers.

4. Give monsters their own combat controller.
   - Add `MonsterCombatController` or attach the same reusable `CombatActionRunner`.
   - Monster attacks should happen from the monster’s own attack timer.

5. Replace paired `ResolveCombatTick`.
   - Remove the current model where adventurer attacks and monster immediately counters.
   - Let each combatant resolve actions when their own timer is ready.

6. Add a simulation clock.
   - Create `SimulationClock` or add one to `GameController`.
   - Emit `simulation_tick`.
   - Use it for low-frequency AI and effect maintenance only.

7. Add one simple skill.
   - Give the adventurer a `heavy_strike` or equivalent.
   - Put it on its own cooldown.
   - Verify it can be used independently from basic attack timing.

8. Add optional cast action.
   - Add one simple spell with cast time.
   - Verify cast starts, counts down, completes, then applies effect.

9. Update HUD and TestBridge state.
   - Show current action, combat state, attack cooldown, skill cooldown, cast remaining, and recovery remaining.
   - Publish `combat_encounter` state with both combatants.

10. Run live verification.
   - Build the project.
   - Run a headless TestBridge session.
   - Confirm independent attack timings through event timestamps and cooldown state.

## Acceptance Criteria

- Adventurer and monster attacks are not paired turns.
- Adventurer and monster each own independent action timers.
- Basic attack speed affects attack cadence.
- At least one cooldown skill exists and matures independently from basic attack timing.
- Optional cast action, if implemented in this slice, starts and completes from a cast timer rather than a simulation tick.
- Simulation tick exists and is visibly separate from combat action timers.
- Simulation tick does not directly trigger basic attacks.
- Combat still resolves death, rewards, return to town, recovery, and repeat loop.
- TestBridge events expose action start, action resolve, cooldown start, cooldown ready, and simulation tick.
- Bridge state exposes per-combatant timers.
- Headless verification proves at least one full loop completes.

## Risks And Constraints

- Do not overbuild the action system into a full RPG rules engine yet.
- Avoid duplicating adventurer-only logic into monster-only logic. Prefer shared action runner behavior.
- Keep formulas explicit and logged.
- Keep bridge events precise enough to debug timing bugs.
- Preserve the current simple visible arena.
- Do not add parties, equipment, inventory, or shops in this slice.

## Open Questions

- Should attack speed be stored as attacks per second or seconds per attack?
- Should global cooldown exist immediately or wait until multiple skills exist?
- Should monsters use the same action list format as adventurers from the start?
- Should buffs and debuffs tick in fixed pulses or store exact expiry timestamps with tick-based cleanup?
- Should regeneration be continuous or pulse-based on simulation ticks?
