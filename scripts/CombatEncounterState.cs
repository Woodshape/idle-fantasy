#nullable enable

using System.Collections.Generic;
using System.Linq;
using GArray = Godot.Collections.Array;
using GDict = Godot.Collections.Dictionary;

public sealed record CombatTickResult(
	long Tick,
	int QueuedActionCount,
	bool EncounterEnded,
	int LivingAdventurerCount,
	int LivingMonsterCount);

public sealed record CombatEncounterState(
	int EncounterId,
	bool Active,
	long LastProcessedTick,
	double TickIntervalSeconds,
	IReadOnlyList<CombatantEncounterState> Adventurers,
	IReadOnlyList<CombatantEncounterState> Monsters)
{
	public GDict ToBridgeDictionary(string source, ICombatant? primaryAdventurer = null)
	{
		CombatantEncounterState? primaryAdventurerState = primaryAdventurer is null
			? Adventurers.FirstOrDefault()
			: Adventurers.FirstOrDefault(state => state.CombatantId == primaryAdventurer.CombatantId) ?? Adventurers.FirstOrDefault();
		CombatantEncounterState? primaryMonsterState = Monsters.FirstOrDefault();

		return new GDict
		{
			{ "source", source },
			{ "encounter_id", EncounterId },
			{ "active", Active },
			{ "last_processed_tick", LastProcessedTick },
			{ "tick_interval_seconds", TickIntervalSeconds },
			{ "adventurer", primaryAdventurerState?.ToBridgeDictionary() ?? CombatantEncounterState.Empty("adventurer").ToBridgeDictionary() },
			{ "monster", primaryMonsterState?.ToBridgeDictionary() ?? CombatantEncounterState.Empty("monster").ToBridgeDictionary() },
			{ "adventurers", BuildCombatantStates(Adventurers) },
			{ "monsters", BuildCombatantStates(Monsters) },
			{ "adventurer_count", Adventurers.Count },
			{ "monster_count", Monsters.Count },
			{ "living_adventurer_count", Adventurers.Count(state => state.IsAlive) },
			{ "living_monster_count", Monsters.Count(state => state.IsAlive) }
		};
	}

	private static GArray BuildCombatantStates(IEnumerable<CombatantEncounterState> states)
	{
		GArray array = new();

		foreach (CombatantEncounterState state in states)
		{
			array.Add(state.ToBridgeDictionary());
		}

		return array;
	}
}

public sealed record CombatantEncounterState(
	string CombatantId,
	string Name,
	string Kind,
	string State,
	string Target,
	string ActiveAction,
	string QueuedAction,
	string LastAction,
	string DefinitionId,
	string CombatLoadoutId,
	IReadOnlyList<string> ActionIds,
	int BasicAttackCooldownTicksRemaining,
	int GlobalCooldownTicksRemaining,
	int CastTicksRemaining,
	int RecoveryTicksRemaining,
	IReadOnlyDictionary<string, int> SkillCooldowns,
	bool IsAlive,
	int AttackSpeed,
	int Health,
	int MaxHealth)
{
	public static CombatantEncounterState Empty(string kind)
	{
		return new CombatantEncounterState(
			string.Empty,
			"none",
			kind,
			CombatState.OutOfCombat.ToString(),
			"none",
			string.Empty,
			string.Empty,
			string.Empty,
			string.Empty,
			string.Empty,
			System.Array.Empty<string>(),
			0,
			0,
			0,
			0,
			new System.Collections.Generic.Dictionary<string, int>(),
			false,
			0,
			0,
			0);
	}

	public GDict ToBridgeDictionary()
	{
		GDict skillCooldowns = new();

		foreach ((string key, int value) in SkillCooldowns)
		{
			skillCooldowns[key] = value;
		}

		return new GDict
		{
			{ "name", Name },
			{ "kind", Kind },
			{ "state", State },
			{ "target", Target },
			{ "active_action", ActiveAction },
			{ "queued_action", QueuedAction },
			{ "last_action", LastAction },
			{ "definition_id", DefinitionId },
			{ "combat_loadout_id", CombatLoadoutId },
			{ "action_ids", BuildActionIds() },
			{ "basic_attack_cooldown_ticks_remaining", BasicAttackCooldownTicksRemaining },
			{ "global_cooldown_ticks_remaining", GlobalCooldownTicksRemaining },
			{ "cast_ticks_remaining", CastTicksRemaining },
			{ "recovery_ticks_remaining", RecoveryTicksRemaining },
			{ "skill_cooldowns", skillCooldowns },
			{ "is_alive", IsAlive },
			{ "attack_speed", AttackSpeed },
			{ "attack_speed_ticks", AttackSpeed },
			{ "health", Health },
			{ "max_health", MaxHealth }
		};
	}

	private GArray BuildActionIds()
	{
		GArray actionIds = new();

		foreach (string actionId in ActionIds)
		{
			actionIds.Add(actionId);
		}

		return actionIds;
	}
}
