#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

[GlobalClass]
public partial class GameContentLibrary : Resource
{
	private readonly Dictionary<string, CombatActionDefinition> _actionsById = new(StringComparer.Ordinal);
	private readonly Dictionary<string, CombatLoadoutDefinition> _loadoutsById = new(StringComparer.Ordinal);
	private readonly Dictionary<string, AdventurerDefinition> _adventurersById = new(StringComparer.Ordinal);
	private readonly Dictionary<string, MonsterDefinition> _monstersById = new(StringComparer.Ordinal);
	private readonly Dictionary<string, EnemyGroupDefinition> _enemyGroupsById = new(StringComparer.Ordinal);

	[Export]
	public CombatActionDefinition[] CombatActions { get; set; } = Array.Empty<CombatActionDefinition>();

	[Export]
	public CombatLoadoutDefinition[] CombatLoadouts { get; set; } = Array.Empty<CombatLoadoutDefinition>();

	[Export]
	public AdventurerDefinition[] Adventurers { get; set; } = Array.Empty<AdventurerDefinition>();

	[Export]
	public MonsterDefinition[] Monsters { get; set; } = Array.Empty<MonsterDefinition>();

	[Export]
	public EnemyGroupDefinition[] EnemyGroups { get; set; } = Array.Empty<EnemyGroupDefinition>();

	[Export]
	public AdventurerSpawnDefinition[] DefaultAdventurerSpawns { get; set; } = Array.Empty<AdventurerSpawnDefinition>();

	[Export]
	public MonsterSpawnDefinition[] DefaultMonsterSpawns { get; set; } = Array.Empty<MonsterSpawnDefinition>();

	public IReadOnlyDictionary<string, CombatActionDefinition> ActionsById => _actionsById;
	public IReadOnlyDictionary<string, CombatLoadoutDefinition> LoadoutsById => _loadoutsById;
	public IReadOnlyDictionary<string, AdventurerDefinition> AdventurersById => _adventurersById;
	public IReadOnlyDictionary<string, MonsterDefinition> MonstersById => _monstersById;
	public IReadOnlyDictionary<string, EnemyGroupDefinition> EnemyGroupsById => _enemyGroupsById;

	public IReadOnlyList<string> ValidateAndBuild()
	{
		List<string> errors = new();
		_actionsById.Clear();
		_loadoutsById.Clear();
		_adventurersById.Clear();
		_monstersById.Clear();
		_enemyGroupsById.Clear();

		IndexById(CombatActions, action => action.ActionId, _actionsById, "combat action", errors);
		IndexById(CombatLoadouts, loadout => loadout.LoadoutId, _loadoutsById, "combat loadout", errors);
		IndexById(Adventurers, adventurer => adventurer.DefinitionId, _adventurersById, "adventurer", errors);
		IndexById(Monsters, monster => monster.DefinitionId, _monstersById, "monster", errors);
		IndexById(EnemyGroups, group => group.GroupId, _enemyGroupsById, "enemy group", errors);

		foreach (CombatActionDefinition action in CombatActions.Where(action => action is not null))
		{
			WarnIfPathDoesNotMatchId(action, action.ActionId, "combat action");
			ValidateAction(action, errors);
		}

		foreach (CombatLoadoutDefinition loadout in CombatLoadouts.Where(loadout => loadout is not null))
		{
			WarnIfPathDoesNotMatchId(loadout, loadout.LoadoutId, "combat loadout");
			ValidateLoadout(loadout, errors);
		}

		foreach (AdventurerDefinition adventurer in Adventurers.Where(adventurer => adventurer is not null))
		{
			WarnIfPathDoesNotMatchId(adventurer, adventurer.DefinitionId, "adventurer");
			ValidateAdventurer(adventurer, errors);
		}

		foreach (MonsterDefinition monster in Monsters.Where(monster => monster is not null))
		{
			WarnIfPathDoesNotMatchId(monster, monster.DefinitionId, "monster");
			ValidateMonster(monster, errors);
		}

		foreach (EnemyGroupDefinition group in EnemyGroups.Where(group => group is not null))
		{
			ValidateEnemyGroup(group, errors);
		}

		foreach (AdventurerSpawnDefinition spawn in DefaultAdventurerSpawns.Where(spawn => spawn is not null && spawn.Enabled))
		{
			if (spawn.ActorDefinition is null)
			{
				errors.Add($"Default adventurer spawn '{FormatSpawnName(spawn.NodeName)}' is missing an actor definition.");
			}
			else if (!_adventurersById.ContainsKey(spawn.ActorDefinition.DefinitionId))
			{
				errors.Add($"Default adventurer spawn '{FormatSpawnName(spawn.NodeName)}' references unregistered adventurer '{spawn.ActorDefinition.DefinitionId}'.");
			}
		}

		foreach (MonsterSpawnDefinition spawn in DefaultMonsterSpawns.Where(spawn => spawn is not null && spawn.Enabled))
		{
			if (spawn.ActorDefinition is null)
			{
				errors.Add($"Default monster spawn '{FormatSpawnName(spawn.NodeName)}' is missing an actor definition.");
			}
			else if (!_monstersById.ContainsKey(spawn.ActorDefinition.DefinitionId))
			{
				errors.Add($"Default monster spawn '{FormatSpawnName(spawn.NodeName)}' references unregistered monster '{spawn.ActorDefinition.DefinitionId}'.");
			}

			if (spawn.EnemyGroup is not null && !_enemyGroupsById.ContainsKey(spawn.EnemyGroup.GroupId))
			{
				errors.Add($"Default monster spawn '{FormatSpawnName(spawn.NodeName)}' references unregistered enemy group '{spawn.EnemyGroup.GroupId}'.");
			}
		}

		return errors;
	}

	public AdventurerDefinition GetAdventurer(string definitionId)
	{
		return _adventurersById[definitionId];
	}

	public MonsterDefinition GetMonster(string definitionId)
	{
		return _monstersById[definitionId];
	}

	public bool TryGetAdventurer(string definitionId, out AdventurerDefinition? definition)
	{
		return _adventurersById.TryGetValue(definitionId, out definition);
	}

	public bool TryGetMonster(string definitionId, out MonsterDefinition? definition)
	{
		return _monstersById.TryGetValue(definitionId, out definition);
	}

	public CombatLoadout CreateActionsForLoadout(CombatLoadoutDefinition loadout, string definitionId)
	{
		return loadout.ToRuntimeLoadout(definitionId);
	}

	private void ValidateAction(CombatActionDefinition action, List<string> errors)
	{
		if (string.IsNullOrWhiteSpace(action.DisplayName))
		{
			errors.Add($"Combat action '{action.ActionId}' is missing a display name.");
		}

		if (action.RequiresTarget && action.Range <= 0.0)
		{
			errors.Add($"Combat action '{action.ActionId}' requires a positive range.");
		}

		if (action.CooldownTicks < 0)
		{
			errors.Add($"Combat action '{action.ActionId}' has negative cooldown ticks.");
		}

		if (action.CastTicks < 0)
		{
			errors.Add($"Combat action '{action.ActionId}' has negative cast ticks.");
		}

		if (action.RecoveryTicks < 0)
		{
			errors.Add($"Combat action '{action.ActionId}' has negative recovery ticks.");
		}

		if (action.ActionWeight < 0)
		{
			errors.Add($"Combat action '{action.ActionId}' has a negative action weight.");
		}

		if (action.DamageMultiplier <= 0.0)
		{
			errors.Add($"Combat action '{action.ActionId}' requires a positive damage multiplier.");
		}
	}

	private void ValidateLoadout(CombatLoadoutDefinition loadout, List<string> errors)
	{
		if (string.IsNullOrWhiteSpace(loadout.DisplayName))
		{
			errors.Add($"Combat loadout '{loadout.LoadoutId}' is missing a display name.");
		}

		if (loadout.Actions.Length == 0)
		{
			errors.Add($"Combat loadout '{loadout.LoadoutId}' must contain at least one action.");
		}

		HashSet<string> actionIds = new(StringComparer.Ordinal);

		foreach (CombatActionDefinition? action in loadout.Actions)
		{
			if (action is null)
			{
				errors.Add($"Combat loadout '{loadout.LoadoutId}' contains a missing action reference.");
				continue;
			}

			if (!_actionsById.ContainsKey(action.ActionId))
			{
				errors.Add($"Combat loadout '{loadout.LoadoutId}' references unregistered action '{action.ActionId}'.");
			}

			if (!actionIds.Add(action.ActionId))
			{
				errors.Add($"Combat loadout '{loadout.LoadoutId}' references duplicate action '{action.ActionId}'.");
			}
		}
	}

	private void ValidateAdventurer(AdventurerDefinition adventurer, List<string> errors)
	{
		if (string.IsNullOrWhiteSpace(adventurer.DisplayName))
		{
			errors.Add($"Adventurer '{adventurer.DefinitionId}' is missing a display name.");
		}

		if (adventurer.Stats is null)
		{
			errors.Add($"Adventurer '{adventurer.DefinitionId}' is missing stats.");
		}
		else
		{
			ValidateStats(adventurer.DefinitionId, adventurer.Stats, errors);
		}

		if (adventurer.CombatLoadout is null)
		{
			errors.Add($"Adventurer '{adventurer.DefinitionId}' is missing a combat loadout.");
		}
		else if (!_loadoutsById.ContainsKey(adventurer.CombatLoadout.LoadoutId))
		{
			errors.Add($"Adventurer '{adventurer.DefinitionId}' references unregistered loadout '{adventurer.CombatLoadout.LoadoutId}'.");
		}

		if (adventurer.MovementSpeed <= 0.0f)
		{
			errors.Add($"Adventurer '{adventurer.DefinitionId}' requires positive movement speed.");
		}

		if (adventurer.StopDistance < 0.0f)
		{
			errors.Add($"Adventurer '{adventurer.DefinitionId}' cannot have negative stop distance.");
		}
	}

	private void ValidateMonster(MonsterDefinition monster, List<string> errors)
	{
		if (string.IsNullOrWhiteSpace(monster.DisplayName))
		{
			errors.Add($"Monster '{monster.DefinitionId}' is missing a display name.");
		}

		if (monster.Stats is null)
		{
			errors.Add($"Monster '{monster.DefinitionId}' is missing stats.");
		}
		else
		{
			ValidateStats(monster.DefinitionId, monster.Stats, errors);
		}

		if (monster.CombatLoadout is null)
		{
			errors.Add($"Monster '{monster.DefinitionId}' is missing a combat loadout.");
		}
		else if (!_loadoutsById.ContainsKey(monster.CombatLoadout.LoadoutId))
		{
			errors.Add($"Monster '{monster.DefinitionId}' references unregistered loadout '{monster.CombatLoadout.LoadoutId}'.");
		}

		if (monster.MovementSpeed <= 0.0f)
		{
			errors.Add($"Monster '{monster.DefinitionId}' requires positive movement speed.");
		}

		if (monster.AggroRange < 0.0f)
		{
			errors.Add($"Monster '{monster.DefinitionId}' cannot have negative aggro range.");
		}

		if (monster.AggroAttackDistance < 0.0f)
		{
			errors.Add($"Monster '{monster.DefinitionId}' cannot have negative aggro attack distance.");
		}
	}

	private static void ValidateEnemyGroup(EnemyGroupDefinition group, List<string> errors)
	{
		if (string.IsNullOrWhiteSpace(group.GroupId))
		{
			errors.Add("Enemy group is missing a group id.");
		}

		if (string.IsNullOrWhiteSpace(group.DisplayName))
		{
			errors.Add($"Enemy group '{group.GroupId}' is missing a display name.");
		}

		if (group.SocialRadius < 0.0f)
		{
			errors.Add($"Enemy group '{group.GroupId}' cannot have a negative social radius.");
		}
	}

	private static void ValidateStats(string ownerId, CombatStatsDefinition stats, List<string> errors)
	{
		if (stats.MaxHealth <= 0)
		{
			errors.Add($"Stats for '{ownerId}' require positive max health.");
		}

		if (stats.Attack < 0)
		{
			errors.Add($"Stats for '{ownerId}' cannot have negative attack.");
		}

		if (stats.Accuracy < 0.0 || stats.Accuracy > 1.0)
		{
			errors.Add($"Stats for '{ownerId}' require accuracy between 0 and 1.");
		}

		if (stats.CritChance < 0.0 || stats.CritChance > 1.0)
		{
			errors.Add($"Stats for '{ownerId}' require crit chance between 0 and 1.");
		}

		if (stats.CritDamage < 1.0)
		{
			errors.Add($"Stats for '{ownerId}' require crit damage of at least 1.");
		}

		if (stats.Defense < 0)
		{
			errors.Add($"Stats for '{ownerId}' cannot have negative defense.");
		}

		if (stats.Evasion < 0.0 || stats.Evasion > 1.0)
		{
			errors.Add($"Stats for '{ownerId}' require evasion between 0 and 1.");
		}

		if (stats.AttackSpeedTicks <= 0)
		{
			errors.Add($"Stats for '{ownerId}' require positive attack speed ticks.");
		}
	}

	private static void IndexById<TDefinition>(
		IEnumerable<TDefinition?> definitions,
		Func<TDefinition, string> getId,
		Dictionary<string, TDefinition> destination,
		string definitionKind,
		List<string> errors)
		where TDefinition : Resource
	{
		foreach (TDefinition? definition in definitions)
		{
			if (definition is null)
			{
				errors.Add($"A {definitionKind} entry is missing.");
				continue;
			}

			string id = getId(definition);

			if (string.IsNullOrWhiteSpace(id))
			{
				errors.Add($"A {definitionKind} entry is missing an id.");
				continue;
			}

			if (!destination.TryAdd(id, definition))
			{
				errors.Add($"Duplicate {definitionKind} id '{id}'.");
			}
		}
	}

	private static void WarnIfPathDoesNotMatchId(Resource resource, string id, string definitionKind)
	{
		if (string.IsNullOrWhiteSpace(resource.ResourcePath) || string.IsNullOrWhiteSpace(id))
		{
			return;
		}

		string fileName = Path.GetFileNameWithoutExtension(resource.ResourcePath);

		if (!string.Equals(fileName, id, StringComparison.Ordinal))
		{
			GD.PushWarning($"{definitionKind} id '{id}' does not match file name '{fileName}'.");
		}
	}

	private static string FormatSpawnName(string nodeName)
	{
		return string.IsNullOrWhiteSpace(nodeName) ? "<unnamed>" : nodeName;
	}
}
