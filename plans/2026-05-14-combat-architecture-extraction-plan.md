# Combat Architecture Extraction Plan

## Goal

Separate the current combat responsibilities into clearer architectural units when the combat loop grows beyond the current folded implementation.

The intended split is:

- `AdventurerCombatController`: Godot-facing adapter for an adventurer's participation in combat.
- `CombatActionRunner`: per-combatant execution state and action lifecycle.
- `CombatEncounter`: the owned active fight.
- `CombatDecisionController`: policy for choosing actions and targets.
- `CombatTickResolver`: deterministic resolution of one authoritative combat tick.

This is a refactor plan, not a requirement to add all classes immediately. The current folded code is acceptable while the loop is small.

## Current Folded Implementation

Today, `AdventurerCombatController` and `CombatActionRunner` carry most responsibilities:

- `AdventurerCombatController` owns encounter membership, starts and stops combat, creates runners, processes simulation ticks, rolls action order, resolves queued actions, handles death or defeat, emits encounter events, and publishes `combat_encounter` state.
- `CombatActionRunner` owns per-combatant state, cooldowns, casts, recovery, target references, queued actions, active actions, deterministic list-priority action selection, range checks, hit and damage formulas, cooldown events, action events, and combatant snapshots.

This works for the current slice, but the responsibilities will become cramped once parties, dynamic joins, smarter class AI, flee/heal/buff logic, status effects, and reward ownership all need to cooperate.

## Target Responsibilities

| Type | Owns | Should Not Own |
| --- | --- | --- |
| `AdventurerCombatController` | Godot node integration, bridge from `AdventurerController` into combat, adventurer-facing state publication, forwarding combat outcomes back to the idle loop. | Initiative resolution, action priority rules, encounter membership rules, reward membership, target scoring. |
| `CombatActionRunner` | One combatant's action lifecycle: counters, queued action, active action, range validation, action start/resolve/cancel events, combatant snapshot. | Encounter-wide end conditions, party membership, global action ordering, high-level AI policy. |
| `CombatEncounter` | Encounter id, participating adventurers and monsters, living membership, dynamic joins, leaving/removal, end conditions, reward membership, encounter state. | Per-frame Godot input, UI labels, action scoring formulas, low-level hit/damage resolution. |
| `CombatDecisionController` | Choosing an intended action and target from current encounter state, actor role, available actions, cooldowns, range, health, class/archetype, and later personality or party tactics. | Mutating health, decrementing counters, emitting resolution events, owning Godot nodes. |
| `CombatTickResolver` | One tick's deterministic order: advance counters, request decisions for ready combatants, queue legal actions, roll order, resolve/cancel actions, collect tick events/results. | Persistent encounter ownership, long-term AI strategy, scene integration. |

## AdventurerCombatController After Extraction

`AdventurerCombatController` should become thin.

Keep:

- `StartCombat` or `JoinCombat` entry points used by `AdventurerController`.
- Godot parent lookup for the owning `Adventurer`.
- Wiring to the active `CombatEncounter`.
- Adventurer-specific bridge state that the idle loop needs.
- Forwarding end results: adventurer died, target defeated, encounter ended, loot phase can begin.

Move out:

- `_encounterAdventurers`, `_encounterMonsters`, `_runners`, encounter id, and living-count end checks.
- `RollActionOrder`.
- dynamic monster join rules.
- target selection callbacks.
- encounter-wide `combat_tick_started` and `combat_tick_completed` ownership.

Reason:

The controller should answer "how does this adventurer node enter and observe combat?" It should not answer "how does a fight resolve?"

## CombatActionRunner After Extraction

`CombatActionRunner` should remain a low-level executor, not become the AI brain.

Keep:

- `CombatState`
- `BasicAttackCooldownTicksRemaining`
- `GlobalCooldownTicksRemaining`
- `CastTicksRemaining`
- `RecoveryTicksRemaining`
- `SkillCooldowns`
- queued and active action lifecycle
- `IsActionInRange`
- `ResolveQueuedAction`
- hit and damage formula execution until a separate combat math service is justified
- combatant snapshot publishing through `ICombatant.SetCombatSnapshot`

Move out:

- `SelectAction`
- deterministic list-priority policy
- target selection policy
- any future class/personality/party strategy

Reason:

The runner should know whether an action can legally execute and how to execute it. It should not decide which action is strategically best.

## CombatEncounter Responsibilities

`CombatEncounter` is the persistent fight object.

It should own:

- stable encounter id
- participants and combatant runners
- current living adventurers and monsters
- dynamic joins from proximity or social aggro
- conflict handling when a monster is already represented by another active encounter
- reward membership for all monsters defeated inside the encounter
- end conditions
- encounter state snapshots

It should expose:

```csharp
public sealed class CombatEncounter
{
    public int EncounterId { get; }
    public IReadOnlyList<Adventurer> Adventurers { get; }
    public IReadOnlyList<Monster> Monsters { get; }
    public bool IsActive { get; }

    public bool TryAddMonster(Monster monster, Adventurer target, string trigger, long tick);
    public CombatTickResult ProcessTick(long tick, double tickIntervalSeconds);
    public CombatEncounterState BuildState();
}
```

## CombatDecisionController Responsibilities

`CombatDecisionController` chooses intent. It should return a decision object and avoid mutating combat directly.

Example shape:

```csharp
public sealed record CombatDecision(
    CombatAction? Action,
    ICombatant? Target,
    string Reason);
```

Inputs should include:

- actor snapshot
- available actions
- current target candidates
- cooldown/cast/recovery state
- range and movement facts
- health and survival pressure
- archetype or monster type
- later: party role, threat, status effects, personality, player possession commands

Initial policies can stay simple:

- warrior: prefer `heavy_strike`, then `basic_attack`
- mage: prefer `spark`, then ranged `basic_attack`
- monster: prefer current `AggroTarget`, then nearest living adventurer, then `basic_attack`

The important change is that these rules live behind a decision interface instead of inside `CombatActionRunner`.

## CombatTickResolver Responsibilities

`CombatTickResolver` resolves one authoritative tick.

Tick order:

1. Advance each runner's counters.
2. Ask `CombatDecisionController` for decisions from ready runners.
3. Queue legal decisions through the runners.
4. Roll action order for queued actions.
5. Resolve queued actions in order.
6. Cancel invalid actions when targets die or move out of range.
7. Return a tick result containing deaths, defeated monsters, events, and state changes.

This keeps "one tick of combat math" separate from both scene integration and encounter lifetime.

## Migration Plan

1. Add passive data shapes.
   - Add `CombatDecision`, `CombatTickResult`, and `CombatEncounterState` records.
   - Keep behavior unchanged.

2. Extract decision policy.
   - Move `CombatActionRunner.SelectAction` and target callbacks into `CombatDecisionController`.
   - Keep the same list-priority behavior first.
   - Verify events remain unchanged.

3. Extract tick resolution.
   - Move action queue collection, order rolling, and queued resolution from `AdventurerCombatController.ProcessSimulationTick` into `CombatTickResolver`.
   - Keep `CombatActionRunner` as the legal executor.

4. Extract encounter ownership.
   - Move participant lists, runner lists, dynamic joins, end conditions, and encounter state publication into `CombatEncounter`.
   - Make `AdventurerCombatController` hold or locate the active encounter instead of owning it directly.

5. Harden multi-combatant behavior.
   - Make dynamic joins update reward membership.
   - Prevent one monster from being represented by multiple active encounters.
   - Add tests or bridge verifiers for 1v2, 2v1, and 2v2 scenes.

## Event And State Expectations

Existing event names should be preserved during extraction unless a rename is intentional and documented:

- `combat_started`
- `combat_tick_started`
- `combat_tick_completed`
- `combat_target_changed`
- `combat_action_queued`
- `combat_action_order_rolled`
- `combat_action_started`
- `combat_action_resolved`
- `combat_action_cancelled`
- `attack_roll_resolved`
- `damage_applied`
- `monster_joined_encounter`
- `monster_defeated`
- `adventurer_died`

`combat_encounter` state should continue exposing:

- encounter id
- active flag
- tick interval
- participants
- living counts
- per-combatant counters
- current targets
- queued and active actions

## Non-Goals

- Do not rewrite combat math while extracting architecture.
- Do not add a data-driven skill library in the same change.
- Do not add party reward splitting until reward membership is reliable.
- Do not replace TestBridge event names unless a verifier is updated in the same change.
- Do not introduce a broad ECS or data framework for this slice.

## Acceptance Criteria

- Existing `scripts/verify_combat_main_loop.sh` still passes.
- Existing bridge events remain available or intentional changes are documented.
- `AdventurerCombatController` no longer owns tick resolution after `CombatTickResolver` exists.
- `CombatActionRunner` no longer chooses strategic actions after `CombatDecisionController` exists.
- `CombatEncounter` owns participants, dynamic joins, end conditions, and reward membership after extraction.
- New multi-combatant verification covers at least one 1v2 or 2v2 scene before relying on dynamic joins for full-loop completion.
