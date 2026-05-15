# Next Gameplay Slices Plan

## Goal

Build the smallest reliable path from the current warrior + mage + slime wave proof-of-concept into a readable idle RPG loop with progression, a town economy hook, player hiring, autonomous target choice, and hardened multi-combatant encounters.

The ordering is intentional: do not build shops, parties, or dungeon-like target selection until the current starter loop is consistently watchable and verified.

## Current Implementation Snapshot

The current foundation already includes:

- A fixed simulation tick driven by `GameController`.
- Data-backed warrior, mage, slime, combat actions, loadouts, and default spawns under `data/`.
- Warrior melee `heavy_strike`, mage ranged `spark`, and slime `basic_attack` behavior.
- Adventurer states for choosing targets, traveling, fighting, collecting loot, returning to town, recovering, and death.
- Town recovery through `Town.Recover` and `adventurer_recovery_tick` / `adventurer_recovered` events.
- Wave clear and respawn through `monster_wave_cleared` and `monster_wave_respawned`.
- TestBridge state for adventurers, monsters, combat encounters, game loop, and simulation clock.
- Early encounter ownership through `CombatEncounter`, `AdventurerCombatController`, and `FindHuntTargets` claim avoidance.

Primary acceptance gate for the next slices remains:

```bash
scripts/verify_combat_main_loop.sh
```

## Slice 1: Reliable Watchable Starter Loop

### Outcome

The current warrior + mage + three-slime wave should be reliably fun and readable in headless verification and when watched in the editor. Adventurers should usually clear the first wave, take visible pressure, collect rewards, return/recover when appropriate, and see the wave respawn.

### Scope

- Tune only the existing data first: warrior, mage, slime stats, slime reward values, slime spacing, and recovery rate.
- Keep the first wave small: no new monster archetypes yet.
- Confirm slime melee behavior after mage ranged opener: slime should close and basic attack within melee range.
- Confirm adventurer survivability without making the wave trivial.
- Confirm full loop evidence: target selection, combat, loot, recovery, wave clear, respawn.
- Strengthen `verify_combat_main_loop.sh` only where it improves confidence in the starter loop, not as a broad integration test.

### Implementation Notes

- Prefer data edits in `data/adventurers/*.tres`, `data/monsters/slime.tres`, and `data/spawns/default_world_content.tres` before changing combat formulas.
- Treat `RestHealthRatio`, `RecoveryHealthPerSecond`, and slime `Attack` / `MaxHealth` / `AttackSpeedTicks` as the first tuning levers.
- If randomness causes flakes, stabilize acceptance around event presence and bounded outcomes rather than exact damage sequences.

### Acceptance

- `scripts/verify_combat_main_loop.sh` passes repeatedly.
- Events include `combat_action_resolved` for adventurers and monsters, `loot_collected`, `adventurer_recovery_tick`, `game_loop_completed`, `monster_wave_cleared`, and `monster_wave_respawned`.
- State shows living adventurers after one completed loop.
- Ranged mage behavior and slime melee response remain covered by verification.

### Out Of Scope

- Leveling.
- Shop UI.
- Manual hiring.
- New target scoring.
- Reward splitting.

## Slice 2: Adventurer Progression

### Outcome

Repeated loops should matter. XP should produce clear level-up moments and small stat growth without introducing class trees or build choices.

### Scope

- Add an XP threshold function, initially simple and deterministic.
- Add a level-up event.
- Increase a small stat set on level-up.
- Show level and XP progress in HUD.
- Publish level, current XP, next threshold, and total earned XP through TestBridge state.

### Proposed Minimal Model

- Keep `Experience` as current unspent XP toward the next level, or add `TotalExperience` if cumulative reporting is needed.
- Start with `xp_to_next_level = 20 + ((level - 1) * 15)`.
- On level-up, increment `Level`, subtract threshold XP, and increase a small set of stats.
- Suggested first stat growth: `MaxHealth +4`, `Attack +1` every level, `Defense +1` every other level.
- Heal only the added max-health delta on level-up, not automatically to full, unless playtest readability needs a stronger moment.

### Implementation Notes

- Put progression behavior on `Adventurer`, likely near `AddRewards`.
- Emit `adventurer_level_up` with old/new level, XP before/after, threshold, and changed stats.
- Update `Adventurer.PublishState` with `xp_to_next_level` and, if added, `total_experience`.
- Update `GameController.UpdateAdventurerHud` to show `Level` and `XP current/threshold`.

### Acceptance

- A verification run can observe at least one `loot_collected` and, with current slime XP or adjusted threshold, one `adventurer_level_up` within a bounded time.
- TestBridge `adventurer` state includes level and XP threshold fields.
- Level-up changes are visible in HUD and state.
- `scripts/verify_combat_main_loop.sh` still passes.

### Out Of Scope

- Talent trees.
- Class-specific growth tables.
- Equipment.
- Party-wide XP splitting.

## Slice 3: Town Gold Sink: Healing Or Potions

### Outcome

Adventure rewards should feed back into survival. An adventurer with gold should recover faster or carry one simple potion; an adventurer without gold should still recover slowly.

### Scope

- Add one town service first: paid faster healing is the most direct fit with current `Town.Recover`.
- Spend adventurer gold at town when injured and able to pay.
- If no gold, use existing slow recovery.
- Emit `town_service_used`, `gold_spent`, and optionally `town_service_unaffordable`.
- Add potion only after paid healing is stable, unless potions are selected as the slice's one service.

### Proposed Minimal Model

- `Town` exports `PaidRecoveryCost = 5`, `PaidRecoveryHeal = 12`, and keeps existing passive recovery.
- On entering recovery, if health is below max and gold is enough, spend once and heal immediately.
- Continue passive recovery after the paid heal if still injured.
- Add `Adventurer.SpendGold(int amount)` to centralize gold mutation and state publishing.

### Potion Variant

- If potions are preferred instead of paid healing, add `PotionCharges` to `Adventurer` with max 1.
- Buy one potion in town when gold is available and no potion is carried.
- Auto-use potion in combat or immediately after combat below a low HP threshold.
- Emit `potion_bought` and `potion_used`.

### Acceptance

- Adventurer gold decreases after using a town service.
- Events include `town_service_used` and `gold_spent`.
- If broke, adventurer still recovers through `adventurer_recovery_tick`.
- `scripts/verify_combat_main_loop.sh` still passes.

### Out Of Scope

- Inventory UI.
- Shopkeeper NPCs.
- Multiple item types.
- Price scaling.

## Slice 4: Manual Tavern Hiring

### Outcome

The player gets the first strategic lever: spend town gold to hire an adventurer from existing definitions.

### Scope

- Add either a simple HUD button or a TestBridge command first. Prefer TestBridge command for acceptance, then UI button if the slice needs visible player control.
- Cost gold from a town/player treasury or from the currently selected/lead adventurer, but choose one owner explicitly before implementation.
- Spawn from existing `AdventurerDefinition` data.
- Emit `hire_requested`, `gold_spent`, `adventurer_hired`, and `hire_failed`.

### Recommended Currency Owner

- Introduce a simple `PlayerGold` or `TownGold` counter on `GameController` before hiring.
- Keep adventurer personal gold for recovery/services unless design explicitly wants pooled party funds.
- When loot is collected, optionally route a portion or all gold to `PlayerGold` only when this slice starts; avoid mixing ownership silently.

### Implementation Notes

- Reuse `GameController.SpawnDefaultAdventurer` logic by extracting a runtime spawn helper that accepts an `AdventurerDefinition` and position.
- Add TestBridge command support in `TestBridge` only if existing commands are insufficient.
- Spawn near `Town.ReturnPosition` with a small offset to avoid overlap.
- Cap or validate duplicate hires only if repeated identical recruits cause problems.

### Acceptance

- TestBridge can request a hire by definition id.
- Gold decreases on successful hire.
- New adventurer appears in `game_loop.adventurer_count` and publishes `adventurer` state.
- Broke hire attempt emits `hire_failed` and does not spawn.
- `scripts/verify_combat_main_loop.sh` still passes.

### Out Of Scope

- Tavern roster UI.
- Recruit rarity.
- Weak fallback recruit unless specifically needed for playtesting.
- Party formation rules beyond adding another autonomous adventurer.

## Slice 5: Target Choice And Risk

### Outcome

Adventurers should feel more autonomous: they choose targets based on more than nearest distance, may overreach, and retreat earlier when hurt.

### Scope

- Add at least one additional monster tier or a meaningful distance/reward distinction.
- Score targets using distance, monster level/reward, adventurer level, and current HP ratio.
- Emit target scoring details for TestBridge.
- Add earlier retreat behavior for hurt adventurers.

### Proposed Target Score

Use a simple transparent score before adding more AI concepts:

```text
score = reward_value
  - distance_penalty
  - danger_penalty
  + level_fit_bonus
  + wounded_caution_adjustment
```

Start with readable heuristics:

- `reward_value = monster.GoldReward + monster.ExperienceReward`.
- `distance_penalty = distance / 80`.
- `danger_penalty = max(0, monster.Level - adventurer.Level) * 10`.
- If adventurer HP is below 50%, strongly prefer lower danger or return to town.

### Implementation Notes

- Replace `FindHuntTargets` nearest-only ordering with a scoring method that can emit `target_scored` or `adventurer_target_scored`.
- Keep `maximumTargets` behavior intact.
- Add retreat by extending `ShouldReturnToTownForRest` and checking it before choosing a new target, not by interrupting every fight initially.
- Combat retreat during active combat can come later unless current deaths are too frequent.

### Acceptance

- TestBridge events show at least two scored targets with different score inputs.
- Adventurers select a target that is not always just nearest when reward/risk justifies it.
- Hurt adventurers return to town earlier than healthy adventurers.
- `scripts/verify_combat_main_loop.sh` still passes.

### Out Of Scope

- Procedural maps.
- Quests.
- Full utility AI framework.
- Dungeon routing.

## Slice 6: Encounter/Party Hardening

### Outcome

Multi-combatant encounters should be trustworthy before real party gameplay depends on them.

### Scope

- Make 1v2, 2v1, and 2v2 intentional single-encounter cases.
- Prevent duplicate monster claims across active encounters.
- Track reward membership explicitly.
- Add reward splitting only after membership is reliable.

### Implementation Notes

- Treat `CombatEncounter` as the authoritative owner of encounter membership.
- Add explicit membership state: encounter id, adventurer ids, monster ids, defeated monster ids, eligible reward recipients.
- Ensure `TryAddAggroMonsterToEncounter` cannot add a monster already owned by another active encounter.
- Ensure `FindHuntTargets` and social aggro use the same ownership checks.
- Move loot membership away from controller-local `_lootedMonsterIds` once encounter reward ownership is reliable.

### Acceptance

- Verification covers 1v2, 2v1, and 2v2 starts as one encounter each.
- No monster appears in two active encounters.
- A defeated monster rewards only the intended encounter membership.
- Reward splitting is added only after the above is stable.
- `scripts/verify_combat_main_loop.sh` still passes, plus a new targeted encounter verification script if needed.

### Out Of Scope

- Party UI.
- Formation control.
- Threat/taunt systems.
- Advanced reward rules.

## Verification Strategy

Keep one always-green baseline:

```bash
scripts/verify_combat_main_loop.sh
```

Add focused verification only when the slice introduces behavior the baseline cannot observe cleanly:

- Progression: check `adventurer_level_up` and XP threshold state.
- Town service: check `town_service_used`, `gold_spent`, and fallback slow recovery.
- Hiring: check command/UI hire path, gold cost, and adventurer count increase.
- Target choice: check scoring events and non-nearest selection when appropriate.
- Encounter hardening: check 1v2, 2v1, 2v2 ownership and reward membership.

Avoid turning `verify_combat_main_loop.sh` into a slow exhaustive suite. It should remain the starter-loop acceptance test.

## Recommended Order Of Work

1. Stabilize and tune the current starter loop until it is visually readable and green repeatedly.
2. Add progression because it makes existing loop repetition meaningful with minimal new systems.
3. Add a paid town recovery service because it connects earned gold back into survivability.
4. Decide currency ownership, then add manual hiring through TestBridge first.
5. Add risk-aware target choice once leveling and healing create meaningful risk/reward inputs.
6. Harden encounter ownership before expanding real party behavior.

## Key Design Decisions To Make Before Implementation

- Should adventurer gold remain personal, or should combat loot feed a player/town treasury for hiring?
- Should level-up heal the adventurer, partially heal, or only increase max health?
- Should the first gold sink be paid healing or one carried potion?
- Should hiring allow duplicate warriors/mages, or should it require unique definitions for now?
- Should active combat retreat exist in the target-risk slice, or should retreat only happen between fights first?

## First Slice Exit Criteria

Slice 1 is complete when the current game can be watched without explaining around it:

- The mage clearly opens at range.
- Slimes visibly pressure adventurers and attack at melee range.
- Warrior and mage usually survive the starter wave.
- Loot changes gold/XP.
- Injured adventurers return and recover.
- Cleared waves respawn.
- The verification script proves those facts without flakes.
