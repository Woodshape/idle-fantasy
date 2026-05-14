#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GDict = Godot.Collections.Dictionary;

public sealed class CombatEncounter
{
	private static readonly Dictionary<string, int> ActiveMonsterEncounters = new(StringComparer.Ordinal);

	private readonly List<Adventurer> _adventurers = new();
	private readonly List<Monster> _monsters = new();
	private readonly List<CombatActionRunner> _runners = new();
	private readonly HashSet<string> _defeatedMonsterEventIds = new(StringComparer.Ordinal);
	private readonly HashSet<string> _deadAdventurerEventIds = new(StringComparer.Ordinal);
	private readonly ICombatLoadoutSource _loadoutSource;
	private readonly RandomNumberGenerator _rng;
	private readonly Action<string, GDict> _emitEvent;
	private readonly Action<Monster, Adventurer, string, long>? _applySceneSocialAggro;
	private readonly CombatTickResolver _tickResolver;
	private readonly string _eventSource;
	private long _lastProcessedTick;
	private double _tickIntervalSeconds = 0.25;

	public CombatEncounter(
		int encounterId,
		IEnumerable<Adventurer> adventurers,
		IEnumerable<Monster> monsters,
		ICombatLoadoutSource loadoutSource,
		RandomNumberGenerator rng,
		Action<string, GDict> emitEvent,
		Action<Monster, Adventurer, string, long>? applySceneSocialAggro = null,
		string eventSource = nameof(AdventurerCombatController))
	{
		EncounterId = encounterId;
		_loadoutSource = loadoutSource;
		_rng = rng;
		_emitEvent = emitEvent;
		_applySceneSocialAggro = applySceneSocialAggro;
		_eventSource = eventSource;
		_tickResolver = new CombatTickResolver(
			rng,
			new CombatDecisionController(),
			emitEvent,
			eventSource);

		_adventurers.AddRange(adventurers
			.Where(adventurer => adventurer.IsAlive)
			.Distinct());

		foreach (Monster monster in monsters.Where(monster => monster.IsAlive).Distinct())
		{
			if (TryClaimMonster(monster))
			{
				_monsters.Add(monster);
			}
		}
	}

	public int EncounterId { get; }
	public IReadOnlyList<Adventurer> Adventurers => _adventurers;
	public IReadOnlyList<Monster> Monsters => _monsters;
	public IReadOnlyList<CombatActionRunner> Runners => _runners;
	public bool IsActive { get; private set; }

	public bool CanStart => _adventurers.Count > 0 && _monsters.Count > 0;

	public void Start(long currentTick)
	{
		_lastProcessedTick = currentTick;
		IsActive = true;

		foreach (Adventurer adventurer in _adventurers)
		{
			CombatActionRunner runner = new(
				adventurer,
				_loadoutSource.ResolveLoadout(adventurer),
				_rng,
				_emitEvent,
				ApplySocialAggro);
			_runners.Add(runner);
			runner.Start(currentTick, SelectMonsterTarget(adventurer));
		}

		foreach (Monster monster in _monsters)
		{
			CombatActionRunner runner = new(
				monster,
				_loadoutSource.ResolveLoadout(monster),
				_rng,
				_emitEvent);
			_runners.Add(runner);
			runner.Start(currentTick, SelectAdventurerTarget(monster));
		}
	}

	public bool TryAddMonster(Monster monster, Adventurer target, string trigger, long tick, string actionId = "")
	{
		if (!IsActive
			|| !monster.IsAlive
			|| !target.IsAlive
			|| !_adventurers.Any(adventurer => ReferenceEquals(adventurer, target)))
		{
			return false;
		}

		if (_monsters.Any(encounterMonster => ReferenceEquals(encounterMonster, monster)))
		{
			monster.SetAggroTarget(target, actionId, trigger, tick);
			return true;
		}

		if (!TryClaimMonster(monster))
		{
			return false;
		}

		_monsters.Add(monster);
		monster.SetAggroTarget(target, actionId, trigger, tick);

		CombatActionRunner runner = new(
			monster,
			_loadoutSource.ResolveLoadout(monster),
			_rng,
			_emitEvent);
		_runners.Add(runner);
		runner.Start(tick, SelectAdventurerTarget(monster));

		_emitEvent("monster_joined_encounter", new GDict
		{
			{ "source", _eventSource },
			{ "encounter_id", EncounterId },
			{ "tick", tick },
			{ "monster", monster.MonsterName },
			{ "target", target.AdventurerName },
			{ "action_id", actionId },
			{ "aggro_trigger", trigger },
			{ "adventurer_count", _adventurers.Count },
			{ "monster_count", _monsters.Count },
			{ "living_monster_count", _monsters.Count(candidate => candidate.IsAlive) }
		});
		return true;
	}

	public CombatTickResult ProcessTick(long tick, double tickIntervalSeconds)
	{
		_tickIntervalSeconds = tickIntervalSeconds;

		if (!IsActive)
		{
			return new CombatTickResult(
				tick,
				0,
				true,
				_adventurers.Count(adventurer => adventurer.IsAlive),
				_monsters.Count(monster => monster.IsAlive));
		}

		_lastProcessedTick = tick;
		return _tickResolver.ResolveTick(
			EncounterId,
			tick,
			tickIntervalSeconds,
			_adventurers,
			_monsters,
			_runners,
			GetTargetCandidates,
			HandleEndConditions);
	}

	public void Stop()
	{
		foreach (CombatActionRunner runner in _runners)
		{
			runner.Stop();
		}

		IsActive = false;
		ReleaseMonsterClaims();
		_runners.Clear();
		_adventurers.Clear();
		_monsters.Clear();
	}

	public CombatEncounterState BuildState()
	{
		return new CombatEncounterState(
			EncounterId,
			IsActive,
			_lastProcessedTick,
			_tickIntervalSeconds,
			_adventurers.Select(BuildCombatantState).ToArray(),
			_monsters.Select(BuildCombatantState).ToArray());
	}

	public CombatActionRunner? GetRunner(ICombatant? combatant)
	{
		if (combatant is null)
		{
			return null;
		}

		return _runners.FirstOrDefault(runner => ReferenceEquals(runner.Owner, combatant));
	}

	private void ApplySocialAggro(Adventurer attacker, Monster primaryTarget, CombatAction action, long currentTick)
	{
		if (!attacker.IsAlive || !primaryTarget.IsAlive)
		{
			return;
		}

		foreach (Monster monster in _monsters.Where(monster => monster.IsAlive))
		{
			string aggroTrigger = ReferenceEquals(monster, primaryTarget)
				? "ability_resolved"
				: "social_aggro";
			monster.SetAggroTarget(attacker, action.ActionId, aggroTrigger, currentTick);
		}

		_applySceneSocialAggro?.Invoke(primaryTarget, attacker, action.ActionId, currentTick);
	}

	private IReadOnlyList<ICombatant> GetTargetCandidates(CombatActionRunner runner)
	{
		if (runner.Owner is Adventurer)
		{
			return _monsters
				.Where(monster => monster.IsAlive)
				.Cast<ICombatant>()
				.ToArray();
		}

		return _adventurers
			.Where(adventurer => adventurer.IsAlive)
			.Cast<ICombatant>()
			.ToArray();
	}

	private bool HandleEndConditions()
	{
		if (!IsActive)
		{
			return true;
		}

		foreach (Adventurer adventurer in _adventurers.Where(adventurer => !adventurer.IsAlive))
		{
			if (_deadAdventurerEventIds.Add(adventurer.CombatantId))
			{
				_emitEvent("adventurer_died", new GDict
				{
					{ "source", _eventSource },
					{ "encounter_id", EncounterId },
					{ "adventurer", adventurer.AdventurerName },
					{ "monster", _monsters.FirstOrDefault(monster => monster.IsAlive)?.MonsterName ?? "none" },
					{ "living_adventurer_count", _adventurers.Count(candidate => candidate.IsAlive) }
				});
			}
		}

		foreach (Monster monster in _monsters.Where(monster => !monster.IsAlive))
		{
			if (_defeatedMonsterEventIds.Add(monster.CombatantId))
			{
				GD.Print($"MONSTER_DEFEATED monster={monster.MonsterName}");
				_emitEvent("monster_defeated", new GDict
				{
					{ "source", _eventSource },
					{ "encounter_id", EncounterId },
					{ "adventurer", _adventurers.FirstOrDefault(adventurer => adventurer.IsAlive)?.AdventurerName ?? _adventurers.FirstOrDefault()?.AdventurerName ?? "none" },
					{ "monster", monster.MonsterName },
					{ "gold_reward", monster.GoldReward },
					{ "experience_reward", monster.ExperienceReward },
					{ "living_monster_count", _monsters.Count(candidate => candidate.IsAlive) }
				});
				monster.PublishState();
			}
		}

		bool anyAdventurerAlive = _adventurers.Any(adventurer => adventurer.IsAlive);
		bool anyMonsterAlive = _monsters.Any(monster => monster.IsAlive);

		if (anyAdventurerAlive && anyMonsterAlive)
		{
			return false;
		}

		foreach (CombatActionRunner runner in _runners)
		{
			runner.Stop();
		}

		IsActive = false;
		ReleaseMonsterClaims();
		return true;
	}

	private Monster? SelectMonsterTarget(Adventurer adventurer)
	{
		return FindNearestLivingCombatant(adventurer, _monsters);
	}

	private Adventurer? SelectAdventurerTarget(Monster monster)
	{
		if (monster.AggroTarget is Adventurer aggroTarget
			&& aggroTarget.IsAlive
			&& _adventurers.Any(adventurer => ReferenceEquals(adventurer, aggroTarget)))
		{
			return aggroTarget;
		}

		return FindNearestLivingCombatant(monster, _adventurers);
	}

	private TCombatant? FindNearestLivingCombatant<TCombatant>(ICombatant actor, IEnumerable<TCombatant> candidates)
		where TCombatant : class, ICombatant
	{
		return candidates
			.Where(candidate => candidate.IsAlive)
			.OrderBy(candidate => GetDistanceSquared(actor, candidate))
			.ThenBy(candidate => candidate.CombatantId, StringComparer.Ordinal)
			.FirstOrDefault();
	}

	private static float GetDistanceSquared(ICombatant actor, ICombatant candidate)
	{
		if (actor is Node2D actorNode && candidate is Node2D candidateNode)
		{
			return actorNode.GlobalPosition.DistanceSquaredTo(candidateNode.GlobalPosition);
		}

		return 0.0f;
	}

	private CombatantEncounterState BuildCombatantState(ICombatant combatant)
	{
		CombatActionRunner? runner = GetRunner(combatant);
		IReadOnlyDictionary<string, int> skillCooldowns = runner?.SkillCooldowns
			?? combatant switch
			{
				Adventurer adventurerCombatant => adventurerCombatant.SkillCooldowns,
				Monster monsterCombatant => monsterCombatant.SkillCooldowns,
				_ => new System.Collections.Generic.Dictionary<string, int>()
			};

		return new CombatantEncounterState(
			combatant.CombatantId,
			combatant.DisplayName,
			combatant.CombatantKind,
			GetCombatantState(runner, combatant),
			runner?.Target?.DisplayName ?? "none",
			runner?.ActiveActionId ?? string.Empty,
			runner?.QueuedActionId ?? string.Empty,
			combatant is Adventurer adventurer4 ? adventurer4.LastActionId : combatant is Monster monster4 ? monster4.LastActionId : string.Empty,
			runner?.DefinitionId ?? string.Empty,
			runner?.CombatLoadoutId ?? string.Empty,
			runner?.ActionIds ?? System.Array.Empty<string>(),
			runner?.BasicAttackCooldownTicksRemaining ?? 0,
			combatant is Adventurer adventurer3 ? adventurer3.GlobalCooldownTicksRemaining : combatant is Monster monster3 ? monster3.GlobalCooldownTicksRemaining : 0,
			combatant is Adventurer adventurer1 ? adventurer1.CastTicksRemaining : combatant is Monster monster1 ? monster1.CastTicksRemaining : 0,
			combatant is Adventurer adventurer2 ? adventurer2.RecoveryTicksRemaining : combatant is Monster monster2 ? monster2.RecoveryTicksRemaining : 0,
			new System.Collections.Generic.Dictionary<string, int>(skillCooldowns, StringComparer.Ordinal),
			combatant.IsAlive,
			combatant.AttackSpeed,
			combatant.Health,
			combatant.MaxHealth);
	}

	private static string GetCombatantState(CombatActionRunner? runner, ICombatant combatant)
	{
		if (runner is not null)
		{
			return runner.State.ToString();
		}

		return combatant switch
		{
			Adventurer adventurer => adventurer.CombatState.ToString(),
			Monster monster => monster.CombatState.ToString(),
			_ => CombatState.OutOfCombat.ToString()
		};
	}

	private bool TryClaimMonster(Monster monster)
	{
		if (ActiveMonsterEncounters.TryGetValue(monster.CombatantId, out int activeEncounterId)
			&& activeEncounterId != EncounterId)
		{
			return false;
		}

		ActiveMonsterEncounters[monster.CombatantId] = EncounterId;
		return true;
	}

	private void ReleaseMonsterClaims()
	{
		foreach (Monster monster in _monsters)
		{
			if (ActiveMonsterEncounters.TryGetValue(monster.CombatantId, out int activeEncounterId)
				&& activeEncounterId == EncounterId)
			{
				ActiveMonsterEncounters.Remove(monster.CombatantId);
			}
		}
	}
}
