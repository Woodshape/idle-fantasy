# Data-Driven Skill And Actor Configuration Plan

## Goal

Move the current hardcoded combat actions, adventurer starting stats, monster starting stats, rewards, and default spawn composition into data assets that can be tuned without editing combat code.

This slice should preserve the current working loop: adventurers leave town, choose monsters, resolve tick-based combat, collect rewards, return to town, recover, and repeat. The implementation should make skills and actor definitions data-driven while keeping mutable runtime state on the existing `Adventurer`, `Monster`, `CombatActionRunner`, and controller types.

## Current Starting Point

The project already has a playable combat loop:

- `GameController` owns the authoritative `0.25` second simulation tick.
- `AdventurerController` owns high-level intent: choose target, travel, fight, collect loot, return, recover.
- `AdventurerCombatController` owns active encounters, action selection, action order rolls, combat tick events, and current hardcoded action factories.
- `CombatActionRunner` consumes an ordered `IReadOnlyList<CombatAction>` and already supports basic attacks, skills, spells, cooldown ticks, cast ticks, recovery ticks, range, action weight, damage multiplier, and global cooldown behavior.
- `Adventurer` and `Monster` expose mutable runtime combat state and currently use exported scalar fields for starting stats.
- `GameController.EnsureDefaultAdventurers` and `GameController.SpawnDefaultMonsters` currently hardcode the default roster, monster wave, stats, positions, names, and sprite colors.
- TestBridge already publishes combat/action/state events that can verify this migration headlessly.

The main non-data-driven seams are:

- `AdventurerCombatController.CreateAdventurerActions`.
- `AdventurerCombatController.CreateMonsterActions`.
- `AdventurerCombatController.CreateHeavyStrike`.
- `AdventurerCombatController.CreateSpark`.
- `CombatAction.BasicAttack`.
- `AdventurerArchetype` branching in action selection.
- Runtime default spawn setup in `GameController`.
- Exported default stat fields on `Adventurer` and `Monster` scenes.

## Design Direction

Use Godot `Resource` assets for the first data-driven layer.

Reasoning:

- Resources are editor-friendly and fit Godot scene workflows.
- They can be assigned through exported fields in scenes without adding file parsing code.
- They keep data inside `res://`, which is easy for Godot export, version control, and TestBridge runs.
- C# can still validate the data into dictionaries and convert it into immutable runtime `CombatAction` objects.

This does not block later JSON, CSV, or mod support. If external tuning files are needed later, they can compile into the same resource/runtime model.

## Slice Scope

Included:

- A skill/action definition resource model.
- A shared combat stat block resource or value object for data definitions.
- Adventurer definition resources.
- Monster definition resources.
- Optional spawn entry resources for the default arena composition.
- A small content library resource assigned to `GameController`.
- Runtime validation for missing ids, duplicate ids, invalid tick values, invalid ranges, and missing actions.
- Migration of `basic_attack`, `heavy_strike`, and `spark` into data assets.
- Migration of default `Warrior`, `Mage`, and `Slime` configuration into data assets.
- TestBridge/state output that includes definition ids and selected action ids.
- Headless verification that the current combat loop still completes.

Excluded for now:

- Inventory and equipment stat modifiers.
- Level-up tables.
- Shops, recruitment UI, and economy tuning.
- Loot tables beyond current gold and experience rewards.
- Buffs, debuffs, damage-over-time, healing-over-time, and effect payloads.
- Player spell data.
- Save/load migration.
- External mod loading.
- A full content editor.

## Proposed Runtime Model

Keep the current runtime classes as the authoritative mutable state. Add data definition classes that only describe starting values and available actions.

### `CombatActionDefinition`

A `Resource` that maps directly onto the current `CombatAction` constructor.

Fields:

- `ActionId`
- `DisplayName`
- `Kind`
- `Range`
- `CooldownTicks`
- `CastTicks`
- `RecoveryTicks`
- `RequiresTarget`
- `CanUseWhileMoving`
- `ActionWeight`
- `DamageMultiplier`
- `UsesGlobalAttackCooldown`

Runtime conversion:

```text
CombatActionDefinition -> CombatAction
```

`CombatAction` can stay immutable. The resource should not be passed directly to `CombatActionRunner`, because runner state and cooldown maps should remain runtime-only.

### `CombatLoadoutDefinition`

A `Resource` that defines ordered action priority.

Fields:

- `LoadoutId`
- `DisplayName`
- `Actions`

The order of `Actions` replaces the current deterministic list priority in `CreateAdventurerActions` and `CreateMonsterActions`.

Examples:

- `warrior_starting`: `heavy_strike`, `basic_attack`
- `mage_starting`: `spark`, `basic_attack_ranged`
- `slime_starting`: `basic_attack`

### `CombatStatsDefinition`

A reusable data shape for starting stats.

Fields:

- `MaxHealth`
- `Attack`
- `Accuracy`
- `Defense`
- `Evasion`
- `Initiative`
- `AttackSpeedTicks`

Runtime conversion:

```text
CombatStatsDefinition + current_health_policy -> CombatStats
```

For this slice, current health should initialize to `MaxHealth`.

### `AdventurerDefinition`

A `Resource` describing an adventurer archetype or starting character template.

Fields:

- `DefinitionId`
- `DisplayName`
- `ArchetypeId`
- `Level`
- `Stats`
- `MovementSpeed`
- `StopDistance`
- `CombatLoadout`
- `SpriteModulate`
- `StartingGold`
- `StartingExperience`

This should replace hardcoded `Warrior` and `Mage` setup in `GameController.EnsureDefaultAdventurers`.

### `MonsterDefinition`

A `Resource` describing a monster type.

Fields:

- `DefinitionId`
- `DisplayName`
- `Level`
- `Stats`
- `CombatLoadout`
- `GoldReward`
- `ExperienceReward`
- `MovementSpeed`
- `AggroRange`
- `AggroAttackDistance`
- `SpriteModulate`

This should replace default `Slime` values and make later monster variants easy to add.

### `ActorSpawnDefinition`

A small `Resource` that describes default scene composition without hardcoding it in `GameController`.

Fields:

- `NodeName`
- `ActorDefinition`
- `Position`
- `PositionIsTownRelative`
- `Enabled`

Use separate typed resources if that is cleaner in C#:

- `AdventurerSpawnDefinition`
- `MonsterSpawnDefinition`

### `GameContentLibrary`

A `Resource` assigned to `GameController`.

Fields:

- `CombatActions`
- `CombatLoadouts`
- `Adventurers`
- `Monsters`
- `DefaultAdventurerSpawns`
- `DefaultMonsterSpawns`

Responsibilities:

- Validate all ids.
- Build lookup dictionaries.
- Resolve loadouts into runtime action lists.
- Provide a single place for `GameController` and combat controllers to request definitions.

Keep this local to `GameController` for now. An autoload can be added later if UI, shops, progression, and tooling all need global content access.

## Data Asset Layout

Recommended file layout:

```text
data/
  combat/
    actions/
      basic_attack.tres
      basic_attack_ranged.tres
      heavy_strike.tres
      spark.tres
    loadouts/
      warrior_starting.tres
      mage_starting.tres
      slime_starting.tres
  adventurers/
    warrior.tres
    mage.tres
  monsters/
    slime.tres
  spawns/
    default_world_content.tres
```

Keep ids stable and lowercase snake case:

- `basic_attack`
- `basic_attack_ranged`
- `heavy_strike`
- `spark`
- `warrior`
- `mage`
- `slime`

The file path and id should usually match. Validation should warn when they do not, but only hard-fail on duplicate ids, missing references, and invalid required values.

## Migration Plan

1. Add data definition resource classes.
   - Add `CombatActionDefinition`.
   - Add `CombatLoadoutDefinition`.
   - Add `CombatStatsDefinition`.
   - Add `AdventurerDefinition`.
   - Add `MonsterDefinition`.
   - Add spawn definition resources if the C# export shape is clean.

2. Add `GameContentLibrary`.
   - Export arrays of each definition type.
   - Validate duplicate ids and missing references in `_Ready` or an explicit `Validate`.
   - Build dictionaries by id.
   - Expose `GetAdventurer`, `GetMonster`, `CreateActionsForLoadout`, and default spawn lists.

3. Add data assets matching current behavior.
   - `basic_attack`: range `48`, basic attack cooldown from owner `AttackSpeedTicks`.
   - `basic_attack_ranged`: range `160`, used by current mage behavior.
   - `heavy_strike`: range `48`, cooldown `12`, recovery `1`, action weight `10`, damage multiplier `1.5`.
   - `spark`: range `160`, cooldown `8`, cast `8`, recovery `0`, action weight `4`, damage multiplier `1.2`.
   - `warrior`: current `CombatStats(8, 0.22, 2, 0.12, 3, 4, 36, 36)`.
   - `mage`: current `CombatStats(7, 0.30, 1, 0.18, 5, 5, 32, 32)`.
   - `slime`: current scene/default slime values unless intentionally rebalanced.

4. Convert runtime actor setup.
   - Add `DefinitionId` to `Adventurer` and `Monster` state.
   - Add `SetupFromDefinition` helpers or extend existing `Setup`.
   - Keep `Health`, cooldowns, targets, aggro target, and combat state runtime-only.
   - Keep existing scalar exports temporarily as fallback values for hand-authored test scenes.

5. Replace hardcoded default spawning.
   - `GameController.EnsureDefaultAdventurers` should read default adventurer spawn data.
   - `GameController.SpawnDefaultMonsters` should read default monster spawn data.
   - Preserve `AutoSpawnDefaultAdventurers` and `AutoSpawnDefaultMonsters`.
   - Preserve test scenes that explicitly place actors by allowing scene-local actors to keep their assigned definitions or fallback scalar values.

6. Replace hardcoded action factories.
   - `AdventurerCombatController.CreateAdventurerActions(adventurer)` should resolve `adventurer.CombatLoadout`.
   - `CreateMonsterActions` should resolve the monster loadout.
   - Keep a small fallback path that returns basic attack if content loading fails, but emit a clear error and bridge event.
   - Prefer changing `CombatActionRunner` inputs as little as possible: it should still receive immutable `CombatAction` runtime objects.

7. Publish content ids through TestBridge.
   - Add `definition_id` to `adventurer` and `monster` states.
   - Add `combat_loadout_id` and `action_ids` to relevant combat snapshots.
   - Add `content_validation_failed` and `content_validation_completed` events when bridge is active.

8. Verify behavior parity.
   - Run `dotnet build`.
   - Run `scripts/verify_combat_main_loop.sh`.
   - Confirm representative events still appear: `combat_action_queued`, `combat_cast_started`, `combat_cast_completed`, `monster_aggro_target_set`, `loot_collected`, and `game_loop_completed`.
   - Confirm `heavy_strike` and `spark` are coming from data definitions, not static factory methods.

## Implementation Notes

- Do not make data definitions mutable during combat. Runtime state belongs on actors and runners.
- Keep action selection deterministic and ordered for this slice. Data should define priority by loadout order, not introduce a new AI scoring layer yet.
- Keep the current tick model: all cooldown, cast, recovery, and combat resolution values remain integer ticks.
- Treat `AttackSpeed` as `AttackSpeedTicks` in data to avoid ambiguity.
- Do not add effect payloads until the effect system exists. Add reserved fields later when they have runtime behavior.
- Avoid coupling data definitions to scene node names. Spawns can have node names, but actor definitions should be reusable.
- Keep stable ids in bridge output so tests do not depend on display names.

## Architectural Questions For Review

### 1. Data Format

Recommended option: Godot `Resource` assets now, with optional JSON import later.

Options:

- Godot resources only for now.
- JSON files loaded directly at runtime.
- Godot resources as runtime format, plus later import tooling from JSON or spreadsheets.

Decision needed: Should this first slice use Godot resources as the authoritative editable data format?

### 2. Basic Attack Modeling

Recommended option: make every basic attack a `CombatActionDefinition`, with a special rule that `BasicAttack` cooldown comes from the owner stat.

Options:

- One shared `basic_attack` action with default melee range.
- Separate `basic_attack` and `basic_attack_ranged` data assets.
- Keep `CombatAction.BasicAttack()` as a code-only fallback and make only skills/spells data-driven.

Decision needed: Should ranged basic attacks be separate data actions, or should range come from the actor/loadout?

### 3. Adventurer Identity

Recommended option: move toward string ids such as `warrior` and `mage`, while keeping `AdventurerArchetype` temporarily as compatibility/UI display.

Options:

- Keep the `AdventurerArchetype` enum and map enum values to definitions.
- Replace enum-driven behavior with `DefinitionId` and `ArchetypeId` strings.
- Use both temporarily, then remove enum branching once all behavior is definition-driven.

Decision needed: Should archetypes become pure data ids in this slice, or should the enum remain part of the model?

### 4. Spawn Data Granularity

Recommended option: add explicit spawn definitions for the default arena now.

Options:

- Only data-drive actor templates and keep default spawn positions in `GameController`.
- Add spawn resources for default adventurers and monsters.
- Use scene-placed actors with exported definitions and avoid runtime default spawn resources.

Decision needed: Should default world composition be data-driven in the same slice, or should this slice only data-drive templates/loadouts?

### 5. Validation Strictness

Recommended option: fail fast in development when required content is invalid, but keep a bridge-visible fallback event for diagnostics.

Options:

- Hard error on invalid content and stop spawning.
- Log errors and fall back to code defaults.
- Log errors, publish bridge events, and skip only invalid entries.

Decision needed: How strict should invalid content handling be while the project is still early?

### 6. Test Scene Compatibility

Recommended option: let test scenes keep scalar override fields temporarily, but prefer definitions for new test scenes.

Options:

- Force all test scenes to use definitions immediately.
- Support definitions plus scalar overrides temporarily.
- Keep old scalar exports indefinitely as debug overrides.

Decision needed: Should existing test scenes be migrated immediately, or should compatibility remain for a few slices?

## Acceptance Criteria

- Skills and spells used by combat are defined in data assets.
- Adventurer and monster starting stats are defined in data assets.
- The default warrior, mage, and slime runtime behavior matches the current loop unless deliberately changed.
- `AdventurerCombatController` no longer owns hardcoded `heavy_strike` or `spark` constructors.
- Loadout order controls deterministic action priority.
- `GameController` can spawn the default arena from data or scene-assigned definitions.
- Actor runtime state remains separate from data definitions.
- TestBridge exposes actor definition ids and action ids.
- Existing combat verification still passes.
- `dotnet build` passes.

## Risks And Constraints

- Godot C# resource exports can be awkward around typed arrays and nested resources; keep the first model simple and adjust to the editor's constraints.
- Data validation must catch missing references early or combat failures will be harder to diagnose.
- Avoid building a general rules engine before buffs, equipment, and effects exist.
- Avoid mixing persistent save state into definition resources.
- Keep the current loop playable after each migration step.

## Deferred Work

- Effect definitions for buffs, debuffs, damage-over-time, healing-over-time, and regeneration.
- Class progression and level scaling.
- Equipment modifiers and inventory-driven skill grants.
- Monster loot tables beyond flat gold and experience.
- Recruitment pools and tavern hiring data.
- Player spell library.
- Content import/export tooling.
- Balance test reports generated from content definitions.
