# Combat Data Unified Workflow Plan

## Goal

Create a small shared prerequisite workflow before the combat architecture extraction and data-driven skill configuration work split into parallel slices.

The shared workflow should stabilize the contract between:

- `AdventurerCombatController`
- `CombatActionRunner`
- the future `CombatDecisionController`
- the future `CombatTickResolver`
- the future `CombatEncounter`
- the future data-backed loadout and actor definition system

This is not a third large feature slice. It is a short alignment pass that removes overlap between the architecture refactor plan and the data-driven configuration plan.

## Why This Exists

The combat architecture plan and the data-driven configuration plan are compatible, but they currently touch the same seams:

- `AdventurerCombatController.CreateAdventurerActions`
- `AdventurerCombatController.CreateMonsterActions`
- `AdventurerCombatController.CreateHeavyStrike`
- `AdventurerCombatController.CreateSpark`
- `CombatActionRunner.SelectAction`
- deterministic list-priority action choice
- combat snapshots and TestBridge output

The architecture plan says not to add a data-driven skill library in the same change. The data-driven plan replaces hardcoded action factories and loadout priority. Without a shared workflow first, the two slices would compete over the same action creation and decision surfaces.

## Desired Outcome

After this workflow:

- action selection has a documented contract that both slices can use
- action loadout priority remains deterministic and behavior-compatible
- `CombatActionRunner` still receives immutable runtime `CombatAction` objects
- data ids can be added to state without requiring data assets immediately
- architecture extraction can move decision and tick ownership without redesigning content loading
- data migration can add Godot `Resource` assets without redesigning tick resolution
- existing bridge events and loop verification remain stable

## Shared Contract

### Runtime Actions

`CombatAction` remains the immutable runtime object used by combat execution.

Keep:

- action id
- display name
- kind
- range
- cooldown ticks
- cast ticks
- recovery ticks
- target requirement
- movement rule
- action weight
- damage multiplier
- global attack cooldown flag

Do not pass Godot `Resource` definitions directly into `CombatActionRunner`.

### Loadout Source

Introduce a narrow loadout source concept before any data asset work.

Suggested shape:

```csharp
public interface ICombatLoadoutSource
{
    CombatLoadout ResolveLoadout(ICombatant combatant);
}

public sealed record CombatLoadout(
    string LoadoutId,
    IReadOnlyList<CombatAction> Actions);
```

Initial implementation can wrap the current hardcoded factories:

- warrior: `heavy_strike`, `basic_attack`
- mage: `spark`, `basic_attack_ranged`
- slime: `basic_attack`

The important contract is that a loadout is an ordered list of runtime actions. The source can be hardcoded first and data-backed later.

### Decision Policy

Action choice should consume an ordered loadout and return intent. It should not create action definitions.

Suggested shape:

```csharp
public sealed record CombatDecision(
    CombatAction? Action,
    ICombatant? Target,
    string Reason);

public interface ICombatDecisionPolicy
{
    CombatDecision ChooseAction(
        ICombatant actor,
        CombatLoadout loadout,
        IReadOnlyList<ICombatant> targetCandidates);
}
```

Initial behavior should preserve current deterministic list-priority:

1. scan loadout actions in order
2. choose the first legal action that is off cooldown and in range
3. choose the current target when still valid
4. fall back to the nearest valid enemy when needed

The future architecture slice can move this policy into `CombatDecisionController`. The future data slice can change where the loadout comes from.

### Tick Resolution

The unified workflow should not extract the full tick resolver yet.

It may add passive result shapes if useful:

```csharp
public sealed record CombatTickResult(...);
public sealed record CombatEncounterState(...);
```

Behavior stays inside the current controller until the architecture slice starts.

### Data Identity

Add optional identity fields only when they do not force the resource system to exist immediately:

- `DefinitionId`
- `CombatLoadoutId`
- `ActionId`

Fallback values can be current stable ids:

- `warrior`
- `mage`
- `slime`
- `warrior_starting`
- `mage_starting`
- `slime_starting`
- `basic_attack`
- `basic_attack_ranged`
- `heavy_strike`
- `spark`

Bridge output may include these ids once the values exist, but tests should continue accepting the current event names and core state fields.

## Unified Workflow

1. Inventory current action creation and selection.
   - Confirm where hardcoded actions are created.
   - Confirm where loadout ordering is used.
   - Confirm which bridge events expose action ids or names today.

2. Define the shared runtime action and loadout contract.
   - Keep `CombatAction` as the runtime execution object.
   - Add `CombatLoadout` or equivalent if it reduces ambiguity.
   - Keep ordered loadout priority as the behavior contract.

3. Wrap current hardcoded factories behind the loadout source.
   - Preserve current warrior, mage, and slime behavior.
   - Do not add Godot resources in this step.
   - Do not remove fallback static factories until parity is proven.

4. Isolate decision intent from action construction.
   - Make selection consume a resolved loadout.
   - Keep list-priority behavior unchanged.
   - Prepare the policy boundary that `CombatDecisionController` will later own.

5. Add passive ids and state fields where safe.
   - Add `DefinitionId`, `CombatLoadoutId`, and stable action ids only if they can default to current behavior.
   - Publish ids through TestBridge only as additive fields.
   - Do not rename existing events.

6. Verify parity.
   - Run `dotnet build`.
   - Run `scripts/verify_combat_main_loop.sh`.
   - Confirm representative events still appear.
   - Confirm `heavy_strike` and `spark` priority remains unchanged.

7. Split the work into two parallel slices.
   - Architecture slice owns decision controller, tick resolver, encounter ownership, dynamic joins, and multi-combatant verification.
   - Data slice owns resource definitions, `GameContentLibrary`, data assets, spawn definitions, validation, and data-backed loadout resolution.

## Parallel Slice Boundaries After This Workflow

### Architecture Slice Owns

- `CombatDecisionController`
- `CombatTickResolver`
- `CombatEncounter`
- encounter membership
- action order resolution
- end conditions
- reward membership
- dynamic joins
- 1v2, 2v1, and 2v2 verification

The architecture slice should not add Godot `Resource` content assets.

### Data Slice Owns

- `CombatActionDefinition`
- `CombatLoadoutDefinition`
- `CombatStatsDefinition`
- `AdventurerDefinition`
- `MonsterDefinition`
- spawn definition resources
- `GameContentLibrary`
- default content assets
- content validation
- data-backed loadout resolution

The data slice should not move tick ownership, encounter membership, or combat end conditions.

## Ordering Recommendation

Do the unified workflow first.

Then the two slices can proceed in either order, but the lower-conflict sequence is:

1. Unified workflow.
2. Data-backed loadouts and actor definitions.
3. Architecture extraction.

Reason:

The data slice is narrower if it only replaces the loadout source behind an already agreed contract. The architecture slice is safer when the decision policy already consumes loadouts instead of hardcoded factory methods.

If multi-combatant behavior is the higher priority, the architecture slice can go first after the unified workflow. In that case, keep the loadout source hardcoded until the data slice replaces it.

## Non-Goals

- Do not add Godot `Resource` data assets in the unified workflow.
- Do not extract full encounter ownership in the unified workflow.
- Do not rewrite combat math.
- Do not rename existing TestBridge events.
- Do not remove scalar scene fallbacks.
- Do not add buffs, effects, equipment, loot tables, or progression systems.

## Acceptance Criteria

- Existing combat loop behavior is unchanged.
- `dotnet build` passes.
- `scripts/verify_combat_main_loop.sh` passes.
- The ordered loadout contract is documented in code or plan notes.
- Current hardcoded action creation is wrapped behind a seam that can become data-backed later.
- Decision policy consumes resolved runtime actions rather than constructing them directly.
- Optional ids are additive and do not break existing bridge consumers.
- The architecture and data-driven plans can be executed without both editing the same action factory code in incompatible ways.

## Follow-Up Plan Updates

After this workflow is accepted, update the two existing plans:

- In the combat architecture plan, mark the shared loadout contract as a prerequisite and start extraction from the decision policy boundary.
- In the data-driven configuration plan, replace direct action factory migration language with replacing the loadout source implementation.
- In both plans, reference this unified workflow as the prerequisite that keeps the slices parallel-safe.
