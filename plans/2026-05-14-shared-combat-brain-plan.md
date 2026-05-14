# Shared Combat Brain And Starter Ranged Balance

## Summary

Current combat action choice is only partially shared. `CombatDecisionController` is used by both sides, but it embeds monster-specific target logic, while adventurer and monster controllers separately decide combat distance.

Refactor this into one shared combat brain for action, target, and desired combat range, while keeping adventurer and monster world intentions separate.

Target behavior: warrior and mage should reliably clear the starter three-slime wave, ranged slimes should visibly use `basic_attack_ranged`, and town recovery should still be exercised.

## Key Changes

- Replace the current `CombatDecisionController` and unused `ListPriorityDecisionPolicy` split with one shared combat policy used by every `CombatActionRunner`.
- Move actor-specific preferences out of action selection:
  - Adventurers provide their current hunt or combat target as preferred target.
  - Monsters provide their current aggro target as preferred target.
  - The shared policy then applies the same order for both: preferred target if valid and action-ready, current target if valid and action-ready, then nearest valid target.
- Add shared combat-distance calculation:
  - Desired distance comes from the actor's usable targeted basic attack range minus a small padding, clamped to at least melee distance.
  - Melee actors remain at `42`.
  - `basic_attack_ranged` actors use about `154` for a `160` range.
- Update adventurer and monster movement to consume the same desired combat distance:
  - Adventurer opening approach and in-combat approach use the shared range helper.
  - Monster aggro movement uses the same helper instead of hardcoded `AggroAttackDistance`.
  - `AggroRange` remains a perception and social trigger, not an attack-range substitute.
- Rebalance starter ranged slimes so the party clears the wave:
  - Keep `basic_attack_ranged` behavior intact.
  - Reduce only the minimum needed starter slime pressure, preferably via data (`Accuracy`, `Attack`, or `AttackSpeedTicks`) rather than combat formulas.
  - Do not weaken warrior or mage action logic to compensate.

## Interfaces

- Introduce a small shared combat context type, for example `CombatDecisionContext`, containing:
  - `CombatActionRunner Runner`
  - `IReadOnlyList<ICombatant> TargetCandidates`
  - `ICombatant? PreferredTarget`
- Replace or revise `ICombatDecisionPolicy` so the active implementation can ask runner readiness and range checks instead of reimplementing them.
- Add a shared helper or service for combat range, for example `CombatPositioning.GetDesiredCombatDistance(ICombatant actor, CombatLoadout loadout, float meleeDistance)`.
- Keep world state machines separate:
  - `AdventurerController` still handles hunt, travel, loot, return, recover, and death.
  - `Monster` still handles proximity/social aggro and aggro movement.
  - Neither decides which combat action to use.

## Test Plan

- Build with `dotnet build "Idle Fantasy.sln"`.
- Main loop:
  - Verify slimes with `slime_starting.tres` pointing at `basic_attack_ranged.tres` queue and resolve `basic_attack_ranged`.
  - Verify at least one slime ranged hit emits `damage_applied`.
  - Verify starter wave completes and emits `game_loop_completed`, `loot_collected`, and `monster_wave_respawned`.
  - Update brittle distance assertions so ranged casts and attacks validate `distance <= range` and "not melee-only" where appropriate, instead of requiring exactly `150+` if combat movement legitimately changes.
- Architecture scenes:
  - Update `combat_2v2` expectations if the existing scene creates two independent 1v1 encounters by design, or update the scene/controller setup so it starts one 2v2 encounter. Pick one behavior and make the test match it.
- Regression checks:
  - Melee warrior still uses `basic_attack` and `heavy_strike` only when in melee range.
  - Mage and ranged slime can act at ranged distance.
  - Monster aggro radius remains authored `AggroRange`, not automatically expanded to action range.

## Assumptions

- The shared combat brain covers combat action, target, and combat-distance decisions only.
- Broader future AI differences remain in world/intention controllers, not in duplicated combat action logic.
- Starter wave balance should favor a reliable first-loop clear, with ranged slimes demonstrating damage but not causing early wipes.
- The current monster-only range patch should be folded into the shared positioning helper rather than kept as a monster-only special case.
