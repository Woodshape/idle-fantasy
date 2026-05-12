# Combat Main Loop Plan

## Goal

Refactor combat to a tick-based simulation loop where the world, adventurers, monsters, action cooldowns, skill cooldowns, spell casts, and combat resolution advance on discrete ticks.

This replaces the previous independent real-time action timer direction. The idle game should feel like a visible simulation, and discrete ticks make combat easier to reason about, easier to test, and cheaper to run at scale.

## Revised Design Decision

Use one authoritative simulation tick for combat and world simulation.

Actions should not resolve continuously from per-frame timers. Instead:

- The simulation tick is the authoritative unit of time.
- Each adventurer and monster updates once per tick.
- Basic attacks, skills, spells, recovery, buffs, debuffs, regeneration, and damage-over-time use integer tick counters.
- A combatant can act only during a tick resolution step.
- If multiple combatants are ready on the same tick, a deterministic resolution mechanic plus a random roll decides action order.

Do not clamp real-time actions to a nearby tick. Ignore the continuous action-timer model and express action timing directly in ticks.

## Runtime Model

Initial shared combat model:

- `Combatant`: shared interface or base component for anything that can fight.
- `CombatStats`: attack, accuracy, defense, evasion, initiative, attack speed, max health, current health.
- `CombatAction`: data describing one usable action.
- `CombatActionCooldown`: integer tick cooldown state.
- `CombatEncounter`: owns active combatants, target relationships, pending actions, and end conditions.
- `CombatDecisionController`: chooses an action for a combatant when its tick counters allow it.
- `CombatTickResolver`: gathers ready actions for the tick, orders them, and resolves them.

Keep the implementation simple. This can start as C# classes and components attached to the existing `Adventurer` and `Monster` nodes rather than a large ECS or data framework.

## Combatant State

Each combatant should own these runtime fields:

- `CombatState`
- `CurrentTarget`
- `QueuedAction`
- `ActiveAction`
- `BasicAttackCooldownTicksRemaining`
- `CastTicksRemaining`
- `RecoveryTicksRemaining`
- `SkillCooldownTicksRemaining`
- `Buffs`
- `Debuffs`
- `IsAlive`
- `IsDisabled`
- `CanAct`

Initial combat states:

- `OutOfCombat`: no active encounter.
- `Engaging`: entering combat and validating target/range.
- `Ready`: can evaluate and choose an available action on the current tick, or wait if no action is currently available.
- `Queued`: selected an action for this tick and is waiting for tick-order resolution.
- `Casting`: cast ticks are counting down.
- `Recovering`: post-action lockout ticks are counting down.
- `Disabled`: unable to act because of a condition.
- `Defeated`: health is zero.

Cooldowns should not be combat states. Cooldowns are tick counters that gate action availability.

## Tick Timing Model

Initial tick interval:

```text
simulation_tick_interval = 0.25 seconds
```

All combat timing is represented as integer ticks.

Example conversions:

```text
cooldown_ticks = ceil(cooldown_seconds / simulation_tick_interval)
cast_ticks = ceil(cast_seconds / simulation_tick_interval)
recovery_ticks = ceil(recovery_seconds / simulation_tick_interval)
```

Initial examples:

```text
adventurer_basic_attack_cooldown = 4 ticks
monster_basic_attack_cooldown = 6 ticks
heavy_strike_cooldown = 4 ticks
heavy_strike_recovery = 1 tick
spark_cast_time = 5 ticks
```

Attack speed can still exist as a stat, but it should be converted into tick cooldowns:

```text
basic_attack_cooldown_ticks = max(1, ceil((1.0 / attacks_per_second) / simulation_tick_interval))
```

## Tick Flow

On every simulation tick:

1. Resolve context for each active encounter.
2. Remove defeated combatants from action consideration.
3. Reduce cooldown, cast, recovery, buff, debuff, regeneration, and damage-over-time tick counters.
4. Complete casts whose `CastTicksRemaining` reached zero and queue the completed action for this tick.
5. Ask each `Ready` combatant to choose an available action.
6. Queue selected actions.
7. Roll and sort queued actions for this tick.
8. Resolve queued actions in order.
9. Apply damage, healing, effects, rewards, death, and encounter end conditions.
10. Publish bridge events and state.

No combatant should attack between ticks.

## Action Queue And Order Resolution

If one combatant is ready on a tick, resolve its queued action.

If multiple combatants are ready on the same tick, calculate an initiative score for each queued action:

```text
initiative_score =
  random(1, 100)
  + initiative
  + round(accuracy * 20)
  + round(evasion * 10)
  + round(attack_speed * 5)
  - action_weight
```

Suggested action weights:

```text
basic_attack = 0
heavy_strike = 10
quick_skill = -5
spell = 15
```

Sort descending by `initiative_score`.

Tie breakers:

1. Higher initiative stat.
2. Higher attack speed.
3. Stable combatant id order.

If a queued action's target is defeated before the action resolves, cancel the action and emit `combat_action_cancelled`.

## Basic Attack Flow

When a combatant enters combat:

1. Set `CurrentTarget`.
2. Enter `Engaging`.
3. Validate target is alive and in range.
4. Enter `Ready`.
5. On the next tick, choose an action.
6. If basic attack is chosen and its cooldown ticks are zero, queue it.
7. During tick resolution, resolve the attack according to initiative order.
8. Start basic attack cooldown ticks.
9. Enter `Recovering` if recovery ticks are greater than zero, otherwise return to `Ready`.

If no action is currently available because all relevant cooldowns are still running, the combatant stays `Ready` and waits for later ticks.

## Skill And Spell Flow

Represent adventurer skills and spells with the same `CombatAction` shape. Monsters can remain simpler in this slice and use only basic attacks.

Suggested fields:

- `ActionId`
- `DisplayName`
- `ActionKind`: `BasicAttack`, `Skill`, `Spell`
- `Range`
- `CooldownTicks`
- `CastTicks`
- `RecoveryTicks`
- `RequiresTarget`
- `CanUseWhileMoving`
- `ActionWeight`
- `HitFormula`
- `DamageFormula`
- `Effects`

Initial examples:

- `basic_attack`: instant, cooldown derived from attack speed in ticks.
- `heavy_strike`: instant, 4 tick cooldown, higher damage, heavier initiative weight.
- `spark`: 5 tick cast time, 12 tick cooldown, magic damage.

The first implementation does not need a large skill library. One basic attack plus one simple cooldown skill is enough to prove the model.

## Decision Rules

Start with deterministic priority rules before adding more advanced AI.

Adventurer decision priority:

1. If dead, stop.
2. If disabled, wait.
3. If casting, continue cast countdown.
4. If recovering, continue recovery countdown.
5. If no valid target, exit combat.
6. If a high-priority skill is ready and conditions match, queue it.
7. If basic attack is ready, queue basic attack.
8. Otherwise wait for a later tick.

Monster decision priority:

1. If dead, stop.
2. If disabled, wait.
3. If recovering, continue recovery countdown.
4. If no valid target, exit combat.
5. If basic attack is ready, queue basic attack.
6. Otherwise wait for a later tick.

## TestBridge Events

Keep existing events:

- `combat_started`
- `combat_state_changed`
- `attack_roll_resolved`
- `damage_applied`
- `monster_defeated`
- `adventurer_died`

Tick-specific events:

- `simulation_tick`
- `combat_tick_started`
- `combat_tick_completed`
- `combatant_ready`
- `combat_action_queued`
- `combat_action_order_rolled`
- `combat_action_started`
- `combat_action_resolved`
- `combat_action_cancelled`
- `combat_action_cooldown_started`
- `combat_action_cooldown_ready`
- `combat_cast_started`
- `combat_cast_completed`
- `combat_recovery_started`
- `combat_recovery_completed`
- `effect_applied`
- `effect_expired`
- `regeneration_applied`

Named states:

- `adventurer`
- `monster`
- `combat_encounter`
- `simulation_clock`

State should expose each combatant's tick counters so tests can verify that combat is resolved only on simulation ticks.

## Implementation Steps

1. Preserve the current independent-timer implementation in git as a reference point.
2. Replace real-time combat cooldowns with integer tick counters.
3. Remove action resolution from per-frame `CombatActionRunner.Update(delta)`.
4. Move combat actor updates into the simulation tick.
5. Add a per-encounter action queue for actions selected during the current tick.
6. Add initiative/order rolling for queued actions.
7. Resolve queued actions in initiative order.
8. Convert basic attack speed into tick cooldowns.
9. Convert `heavy_strike` cooldown and recovery into tick counters.
10. Publish tick-specific bridge events and state.
11. Update HUD labels to show tick counters rather than fractional seconds.
12. Run headless TestBridge verification.

## Acceptance Criteria

- Combat actions resolve only on simulation ticks.
- Adventurer and monster updates are processed once per tick.
- Basic attacks, skills, spells, casts, and recovery use integer tick counters.
- The game does not run per-combatant per-frame action timers.
- Basic attack speed affects attack cadence through tick cooldown conversion.
- At least one cooldown skill exists and matures through tick counters.
- If multiple actors queue actions on the same tick, initiative/order resolution decides action order.
- If a target dies before a later queued action resolves, that action is cancelled.
- Combat still resolves death, rewards, return to town, recovery, and repeat loop.
- TestBridge events expose tick start, tick complete, action queue, order roll, action resolve, cooldown start, cooldown ready, and simulation tick.
- Bridge state exposes per-combatant tick counters.
- Headless verification proves at least one full loop completes.

## Risks And Constraints

- Do not overbuild the action system into a full RPG rules engine yet.
- Keep the tick interval explicit and easy to tune.
- Keep formulas explicit and logged.
- Keep bridge events precise enough to debug ordering bugs.
- Preserve the current simple visible arena.
- Do not add parties, equipment, inventory, or shops in this slice.

## Resolved Decisions

- Combat timing should be tick-based for now.
- Do not clamp continuous action timers to ticks; remove continuous combat timing instead.
- Use one simulation tick for world and combat updates in this slice.
- Store attack speed as attacks per second, but convert it to basic attack cooldown ticks.
- Do not add a global cooldown in this slice.
- Do not make monsters use the same action list format as adventurers yet.
- Buffs, debuffs, regeneration, damage-over-time, and healing-over-time tick on the same simulation tick.
