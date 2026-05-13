# Combat Main Loop Plan

## Goal

Keep combat on one authoritative simulation tick where the world, adventurers, monsters, action cooldowns, skill cooldowns, casts, recovery, and combat resolution advance in discrete steps.

This direction is now partially implemented. The current code no longer uses independent per-combatant real-time attack timers for combat resolution. `GameController` emits a fixed simulation tick every 0.25 seconds and passes that tick to the active combat controller.

## Current Implementation Snapshot

The current combat loop is implemented by these main types:

- `GameController`: owns the 0.25 second simulation clock, increments `SimulationTickCount`, emits `simulation_tick`, and calls `AdventurerCombatController.ProcessSimulationTick`.
- `AdventurerController`: runs the higher-level idle loop: choose target, travel, start combat, collect loot, return to town, recover, and repeat.
- `AdventurerCombatController`: owns the active one-adventurer versus one-monster encounter, creates the combat runners, starts and stops encounters, processes each simulation tick, rolls action order, resolves queued actions, emits encounter events, and publishes `combat_encounter` state.
- `CombatActionRunner`: owns per-combatant combat state, target, queued action, active action, basic attack cooldown, global cooldown, cast ticks, recovery ticks, skill cooldowns, action selection, action resolution, hit/damage formulas, cooldown events, and combatant snapshots.
- `CombatAction`: describes usable actions with id, name, kind, range, cooldown ticks, cast ticks, recovery ticks, target requirement, movement flag, initiative weight, damage multiplier, and whether the action uses the owner's basic attack cooldown.
- `ICombatant`, `Adventurer`, and `Monster`: expose shared combat stats and state publishing.

There is not a separate `CombatEncounter`, `CombatDecisionController`, `CombatTickResolver`, or `CombatActionCooldown` class yet. Those responsibilities are folded into `AdventurerCombatController` and `CombatActionRunner`.

## Implemented Runtime Model

Current combatants expose:

- `CombatState`
- `CurrentCombatTargetName`
- `QueuedActionId`
- `ActiveActionId`
- `BasicAttackCooldownTicksRemaining`
- `GlobalCooldownTicksRemaining`
- `CastTicksRemaining`
- `RecoveryTicksRemaining`
- `SkillCooldowns`
- `IsAlive`
- `IsDisabled`
- `CanAct`

Current shared stats are represented by `CombatStats`:

- `Attack`
- `Accuracy`
- `Defense`
- `Evasion`
- `Initiative`
- `AttackSpeedTicks`
- `MaxHealth`
- `CurrentHealth`

`AttackSpeed` on `Adventurer` and `Monster` is currently treated as a tick count, not attacks per second. The adventurer starts with `AttackSpeed = 4`, and the default monster starts with `AttackSpeed = 8`.

## Combat States

Implemented combat states:

- `OutOfCombat`
- `Engaging`
- `Ready`
- `Queued`
- `Casting`
- `Recovering`
- `Disabled`
- `Defeated`

Actions move from `Queued` to immediate resolution inside the tick, with `combat_action_started` and `combat_action_resolved` events emitted during that resolution. There is no separate `UsingAction` state in this slice.

Cooldowns are not states. They are integer tick counters that gate action availability.

## Tick Timing Model

Current tick interval:

```text
simulation_tick_interval = 0.25 seconds
```

All current combat timing is represented as integer ticks.

Current examples:

```text
adventurer_basic_attack_cooldown = adventurer.AttackSpeed = 4 ticks
monster_basic_attack_cooldown = monster.AttackSpeed = 8 ticks
heavy_strike_cooldown = 12 ticks
heavy_strike_recovery = 1 tick
spark_cooldown = 8 ticks
spark_cast = 12 ticks
global_cooldown_after_skill_or_spell = 4 ticks
```

The earlier design note about converting attacks per second into cooldown ticks is not how the implementation currently works. `AttackSpeed` already stores the cooldown duration in ticks:

```text
basic_attack_cooldown_ticks = max(1, owner.AttackSpeed)
```

## Tick Flow

On every simulation tick, `GameController`:

1. Accumulates physics delta until 0.25 seconds have elapsed.
2. Increments `SimulationTickCount`.
3. Emits `simulation_tick`.
4. Calls `AdventurerCombatController.ProcessSimulationTick`.
5. Publishes `simulation_clock` state.

For the active encounter, `AdventurerCombatController.ProcessSimulationTick`:

1. Emits `combat_tick_started`.
2. Advances adventurer and monster runner counters.
3. Lets each runner queue one action if it is ready.
4. Rolls action order for all queued actions on the tick.
5. Resolves queued actions in initiative order.
6. Handles adventurer death or monster defeat.
7. Publishes `combat_encounter` state.
8. Emits `combat_tick_completed`.

No combatant attacks between simulation ticks.

## Runner Counter Flow

Each `CombatActionRunner.AdvanceTickCounters` call:

1. Ignores `OutOfCombat` and `Defeated` runners.
2. Moves a dead owner to `Defeated`.
3. Clears combat if the target is missing or defeated.
4. Moves `Engaging` to `Ready`.
5. Decrements basic attack cooldown, global cooldown, and skill cooldowns.
6. Emits cooldown-ready events when counters reach zero.
7. Decrements recovery and emits `combat_recovery_completed` when recovery ends.
8. Decrements cast time and queues the completed cast when it reaches zero.
9. Publishes the combatant snapshot.

The runner does not currently tick buffs, debuffs, regeneration, damage-over-time, or healing-over-time.

## Current Actions

The adventurer action list is:

- `heavy_strike`: `Skill`, range 48, cooldown 12 ticks, cast 0 ticks, recovery 1 tick, action weight 10, damage multiplier 1.5, does not use the basic attack cooldown.
- `spark`: `Spell`, range 160, cooldown 8 ticks, cast 12 ticks, recovery 0 ticks, action weight 4, damage multiplier 1.2, does not use the basic attack cooldown.
- `basic_attack`: `BasicAttack`, range 48, cooldown from `AttackSpeed`, cast 0 ticks, recovery 0 ticks, action weight 0, damage multiplier 1.0, uses the basic attack cooldown.

The monster action list is:

- `basic_attack`: same action shape as the adventurer basic attack, with cooldown from the monster's `AttackSpeed`.

The runner supports `Spell` and nonzero `CastTicks`; `spark` exercises that path in the live loop.

`Range` and `CanUseWhileMoving` are present on `CombatAction`. The runner enforces `Range` when selecting, keeping, queueing, and resolving an action. `CanUseWhileMoving` is still stored for later movement-aware actions. Range is also supported by `AdventurerController`, which derives the opening approach distance from the longest adventurer action range before starting combat, then closes toward `MeleeApproachDistance` during combat. With the current action list this means the opener uses `spark` range, so melee actions at 48px are out of range at engagement while `spark` can cast from medium range. The melee approach distance is 42px, so follow-up melee attacks and monster retaliation still happen. Adventurer movement is paused while `CombatState` is `Casting`.

## Aggro Rules

Current monster aggro triggers:

1. Proximity aggro: if a living adventurer enters the monster's `AggroRange`, the monster targets that adventurer and starts moving toward `AggroAttackDistance`.
2. Ability aggro: when an adventurer action resolves and hits a monster, the monster targets that adventurer and starts moving toward `AggroAttackDistance`.

Deferred aggro work:

- Social aggro: when a member of a monster group is damaged or affected by an ability or spell, nearby/grouped monsters should also aggro the responsible adventurer.

## Decision Rules

Current action selection is deterministic and list ordered:

1. The runner can act only when state is `Ready`, the global cooldown is zero, the owner is alive, the target is alive, and the action target is in range.
2. The runner scans its action list in order.
3. The first ready action is selected.
4. If the action has cast ticks, casting starts and the action queues when the cast completes.
5. Otherwise the action queues immediately for the current tick.

This means the adventurer prefers `heavy_strike` whenever it is ready, then `spark`, then `basic_attack`. Monsters currently use only `basic_attack`.

## Action Queue And Order Resolution

If one combatant queues an action on a tick, that action resolves on that tick.

If multiple combatants queue actions on the same tick, `AdventurerCombatController` calculates:

```text
initiative_score =
  random(1, 100)
  + initiative
  + max(0, 12 - max(1, attack_speed_ticks))
  - action_weight
```

Actions sort by:

1. Higher `initiative_score`.
2. Higher `Initiative`.
3. Lower `AttackSpeed` tick value.
4. Stable `CombatantId` string order.

If a queued action is no longer valid, the runner emits `combat_action_cancelled`.

## Hit And Damage Formulas

Current hit chance:

```text
hit_chance = clamp(0.75 + attacker_accuracy - defender_evasion, 0.05, 0.95)
```

Current damage:

```text
damage = hit ? max(1, round(attacker_attack * action_multiplier) - defender_defense) : 0
```

`attack_roll_resolved` includes the formula strings, hit chance, roll, hit result, and damage. `damage_applied` includes the final applied damage.

## Death, Rewards, And Loop Continuation

When the adventurer dies:

- `AdventurerCombatController` emits `adventurer_died`, stops both runners, clears the target, and publishes encounter state.
- `AdventurerController` can also move the idle-loop state to `Dead` and emit its own `adventurer_died` event.

When the monster dies:

- `AdventurerCombatController` emits `monster_defeated`, stops both runners, publishes the defeated monster state, clears the target, and publishes encounter state.
- `AdventurerController` moves to `CollectLoot`, adds gold and experience, emits `loot_collected`, then either chooses another target or returns to town for rest.
- `GameController.NotifyLoopCompleted` respawns defeated monsters at their home positions after town recovery.

When a monster is attacked by an adventurer:

- `CombatActionRunner` tells the monster which adventurer attacked it when the action resolves.
- `Monster` stores that adventurer as its aggro target, emits `monster_aggro_target_set` with an `aggro_trigger`, and moves toward its target until it reaches `AggroAttackDistance`.
- `monster_aggro_moving` and `monster_aggro_arrived` events expose the movement path to TestBridge.

## TestBridge Events

Current events include:

- `simulation_tick`
- `combat_started`
- `combat_tick_started`
- `combat_tick_completed`
- `combat_state_changed`
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
- `attack_roll_resolved`
- `damage_applied`
- `monster_defeated`
- `adventurer_died`
- `loot_collected`
- `game_loop_completed`

Events planned earlier but not currently implemented include:

- `effect_applied`
- `effect_expired`
- `regeneration_applied`

## TestBridge State

Current named states include:

- `adventurer`
- `monster`
- `combat_encounter`
- `simulation_clock`
- `game_loop`

Combat state exposes tick counters for basic attack cooldown, global cooldown, cast time, recovery time, and per-skill cooldowns.

## Implementation Status

Completed:

- One authoritative 0.25 second simulation tick exists in `GameController`.
- Combat resolution is driven from simulation ticks.
- Adventurer and monster runners process once per combat tick.
- Basic attacks use integer tick cooldowns.
- `heavy_strike` exists as a cooldown skill.
- `spark` exists as a medium-range 12-tick cast spell and exercises `combat_cast_started` and `combat_cast_completed`.
- Skill cooldown, global cooldown, recovery, and cast counters are integer ticks.
- `CombatAction.Range` is enforced by the runner for selected, queued, and resolving actions.
- Queued actions resolve in rolled initiative order.
- Target death can cancel later queued actions.
- HUD labels show tick counters.
- TestBridge events and state expose the tick loop and per-combatant counters.
- Monsters aggro onto the adventurer attacking them and move into attack distance.
- `scripts/verify_combat_main_loop.sh` launches a headless bridge run and verifies representative combat events through `game_loop_completed`.
- The first idle loop still supports death, monster defeat, loot, return to town, recovery, respawn, and repeat.

Partially complete:

- `CanUseWhileMoving` is stored on actions but not enforced by the runner.
- Social aggro is tracked as backlog.
- The encounter model supports only one adventurer and one monster.
- Decision logic is deterministic list priority, not a separate controller.

Not implemented:

- Buffs, debuffs, regeneration, damage-over-time, healing-over-time, and effects.
- A separate encounter object that can handle parties or multiple enemies.
- A data-driven skill library.
- Equipment, inventory, shops, party composition, or progression systems beyond current gold and experience counters.

## Remaining Work

The open implementation decisions for this slice are settled by the current code:

- Keep `AttackSpeed` as a basic-attack cooldown measured in ticks.
- Keep the 4-tick global cooldown for skills and spells in this slice.
- Keep effect ticking and encounter extraction deferred until the basic loop is verified.

Completed verification for this slice:

1. `scripts/verify_combat_main_loop.sh` launches the world through TestBridge, waits for `game_loop_completed`, and checks representative events: `simulation_tick`, `combat_tick_started`, `combat_action_queued`, `combat_action_order_rolled`, `combat_action_resolved`, `combat_action_cooldown_started`, `combat_action_cooldown_ready`, `combat_cast_started`, `adventurer_cast_movement_paused`, `combat_cast_completed`, `monster_aggro_target_set`, `monster_aggro_moving`, `combat_tick_completed`, `loot_collected`, `game_loop_completed`, and `bridge_stopped`. It also verifies `spark` casts from beyond melee range while still inside its medium range, clears pending adventurer movement during the cast, and only then triggers monster aggro, monster retaliation, and an adventurer melee action after `spark` resolves.
2. A passing run on May 13, 2026 wrote artifacts to `/tmp/idle-fantasy-combat.lob14h`.

Deferred after this slice:

- Add buff, debuff, regeneration, damage-over-time, healing-over-time, and effect ticking.
- Add social aggro for monster groups when a group member is damaged or affected by an ability or spell.
- Extract `CombatEncounter` or `CombatTickResolver` when multi-combatant encounters make the current controller too cramped.
- Revisit attacks-per-second conversion only when equipment, class, or stat presentation needs a player-facing speed value.

## Acceptance Criteria For This Slice

- Combat actions resolve only on simulation ticks.
- Adventurer and monster updates are processed once per combat tick.
- Basic attacks and `heavy_strike` use integer tick counters.
- `spark` uses a 12-tick integer cast plus integer skill cooldown and global cooldown counters.
- The game does not run per-combatant per-frame action timers.
- Basic attack cadence comes from `AttackSpeed` tick values.
- At least one cooldown skill exists and matures through tick counters.
- At least one live spell action casts and resolves through tick counters.
- Action range is enforced before selected or queued actions resolve.
- An attacked monster aggros onto the attacking adventurer and moves into attack distance.
- If multiple actors queue actions on the same tick, initiative/order resolution decides action order.
- If a target dies before a later queued action resolves, that action is cancelled.
- Combat still resolves death, rewards, return to town, recovery, respawn, and repeat loop.
- TestBridge events expose tick start, tick complete, action queue, order roll, action resolve, cooldown start, cooldown ready, cast start, cast complete, and simulation tick.
- Bridge state exposes per-combatant tick counters.
- Headless verification proves at least one full loop completes.

## Risks And Constraints

- Do not grow the action system into a full RPG rules engine yet.
- Keep the tick interval explicit and easy to tune.
- Keep formulas explicit and logged.
- Keep bridge events precise enough to debug ordering bugs.
- Preserve the current simple visible arena.
- Avoid introducing parties, equipment, inventory, or shops until the combat loop is verified.

## Resolved Decisions

- Combat timing is tick-based for now.
- Continuous combat action timers are no longer the intended direction.
- One simulation tick drives world and combat updates in this slice.
- `AttackSpeed` currently means basic attack cooldown ticks.
- Adventurers and monsters both use the same `CombatAction` and `CombatActionRunner` shape.
- Skills and spells trigger a 4-tick global cooldown in the current implementation.
- Buffs, debuffs, regeneration, damage-over-time, and healing-over-time should tick on the same simulation tick when added later.
