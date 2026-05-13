#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GArray = Godot.Collections.Array;
using GDict = Godot.Collections.Dictionary;

public partial class AdventurerCombatController : Node
{
	private readonly RandomNumberGenerator _rng = new();
	private readonly List<Adventurer> _encounterAdventurers = new();
	private readonly List<Monster> _encounterMonsters = new();
	private readonly List<CombatActionRunner> _runners = new();
	private readonly HashSet<string> _defeatedMonsterEventIds = new(StringComparer.Ordinal);
	private readonly HashSet<string> _deadAdventurerEventIds = new(StringComparer.Ordinal);
	private Adventurer? _adventurer;
	private int _encounterId;
	private long _lastProcessedTick;
	private double _tickIntervalSeconds = 0.25;
	private bool _encounterActive;

	public CombatState State => GetRunner(_adventurer)?.State ?? _adventurer?.CombatState ?? CombatState.OutOfCombat;
	public int AttackCooldownTicksRemaining => GetRunner(_adventurer)?.BasicAttackCooldownTicksRemaining ?? 0;
	public IReadOnlyList<Adventurer> EncounterAdventurers => _encounterAdventurers;
	public IReadOnlyList<Monster> EncounterMonsters => _encounterMonsters;
	public bool HasActiveEncounter => _encounterActive;
	public bool HasLivingMonsters => _encounterMonsters.Any(monster => monster.IsAlive);

	public override void _Ready()
	{
		_rng.Randomize();
		_adventurer = GetParentOrNull<Adventurer>();
	}

	public void StartCombat(Monster target, long currentTick)
	{
		_adventurer ??= GetParentOrNull<Adventurer>();

		if (_adventurer is null)
		{
			return;
		}

		StartCombat(new[] { _adventurer }, new[] { target }, currentTick);
	}

	public void StartCombat(IEnumerable<Adventurer> adventurers, IEnumerable<Monster> monsters, long currentTick)
	{
		_adventurer ??= GetParentOrNull<Adventurer>();
		List<Adventurer> liveAdventurers = adventurers
			.Where(adventurer => adventurer.IsAlive)
			.Distinct()
			.ToList();
		List<Monster> liveMonsters = monsters
			.Where(monster => monster.IsAlive)
			.Distinct()
			.ToList();

		if (liveAdventurers.Count == 0 || liveMonsters.Count == 0)
		{
			return;
		}

		StopCombat();
		_encounterAdventurers.AddRange(liveAdventurers);
		_encounterMonsters.AddRange(liveMonsters);
		_defeatedMonsterEventIds.Clear();
		_deadAdventurerEventIds.Clear();
		_encounterId++;
		_lastProcessedTick = currentTick;
		_encounterActive = true;

		foreach (Adventurer adventurer in _encounterAdventurers)
		{
			CombatActionRunner runner = new(
				adventurer,
				CreateAdventurerActions(adventurer),
				_rng,
				EmitBridgeEvent,
				() => SelectMonsterTarget(adventurer),
				ApplySocialAggro);
			_runners.Add(runner);
			runner.Start(currentTick);
		}

		foreach (Monster monster in _encounterMonsters)
		{
			CombatActionRunner runner = new(
				monster,
				CreateMonsterActions(),
				_rng,
				EmitBridgeEvent,
				() => SelectAdventurerTarget(monster));
			_runners.Add(runner);
			runner.Start(currentTick);
		}

		GD.Print($"COMBAT_STARTED tick={currentTick} encounter={_encounterId} adventurers={_encounterAdventurers.Count} monsters={_encounterMonsters.Count}");
		EmitBridgeEvent("combat_started", new GDict
		{
			{ "source", nameof(AdventurerCombatController) },
			{ "encounter_id", _encounterId },
			{ "tick", currentTick },
			{ "adventurer", _encounterAdventurers[0].AdventurerName },
			{ "monster", _encounterMonsters[0].MonsterName },
			{ "adventurers", BuildCombatantNames(_encounterAdventurers) },
			{ "monsters", BuildCombatantNames(_encounterMonsters) },
			{ "adventurer_count", _encounterAdventurers.Count },
			{ "monster_count", _encounterMonsters.Count },
			{ "adventurer_attack_speed_ticks", _encounterAdventurers[0].AttackSpeed },
			{ "monster_attack_speed_ticks", _encounterMonsters[0].AttackSpeed },
			{ "tick_interval_seconds", _tickIntervalSeconds },
			{ "timing_model", "simulation_ticks" }
		});
		PublishEncounterState();
	}

	private void ApplySocialAggro(Adventurer attacker, Monster primaryTarget, CombatAction action, long currentTick)
	{
		if (!attacker.IsAlive || !primaryTarget.IsAlive)
		{
			return;
		}

		foreach (Monster monster in _encounterMonsters.Where(monster => monster.IsAlive))
		{
			string aggroTrigger = ReferenceEquals(monster, primaryTarget)
				? "ability_resolved"
				: "social_aggro";
			monster.SetAggroTarget(attacker, action.ActionId, aggroTrigger, currentTick);
		}

		(GetTree().CurrentScene as GameController)?.ApplySocialAggro(primaryTarget, attacker, action.ActionId, currentTick);
	}

	public bool TryAddAggroMonster(Monster monster, Adventurer aggroTarget, string aggroTrigger, long currentTick, string actionId = "")
	{
		if (!_encounterActive
			|| !monster.IsAlive
			|| !aggroTarget.IsAlive
			|| !_encounterAdventurers.Any(adventurer => ReferenceEquals(adventurer, aggroTarget)))
		{
			return false;
		}

		if (_encounterMonsters.Any(encounterMonster => ReferenceEquals(encounterMonster, monster)))
		{
			monster.SetAggroTarget(aggroTarget, actionId, aggroTrigger, currentTick);
			return true;
		}

		_encounterMonsters.Add(monster);
		monster.SetAggroTarget(aggroTarget, actionId, aggroTrigger, currentTick);

		CombatActionRunner runner = new(
			monster,
			CreateMonsterActions(),
			_rng,
			EmitBridgeEvent,
			() => SelectAdventurerTarget(monster));
		_runners.Add(runner);
		runner.Start(currentTick);

		EmitBridgeEvent("monster_joined_encounter", new GDict
		{
			{ "source", nameof(AdventurerCombatController) },
			{ "encounter_id", _encounterId },
			{ "tick", currentTick },
			{ "monster", monster.MonsterName },
			{ "target", aggroTarget.AdventurerName },
			{ "action_id", actionId },
			{ "aggro_trigger", aggroTrigger },
			{ "adventurer_count", _encounterAdventurers.Count },
			{ "monster_count", _encounterMonsters.Count },
			{ "living_monster_count", _encounterMonsters.Count(candidate => candidate.IsAlive) }
		});
		PublishEncounterState();
		return true;
	}

	public void ProcessSimulationTick(long tick, double tickIntervalSeconds)
	{
		_tickIntervalSeconds = tickIntervalSeconds;

		if (!_encounterActive)
		{
			return;
		}

		_lastProcessedTick = tick;
		EmitBridgeEvent("combat_tick_started", new GDict
		{
			{ "source", nameof(AdventurerCombatController) },
			{ "encounter_id", _encounterId },
			{ "tick", tick },
			{ "tick_interval_seconds", tickIntervalSeconds },
			{ "adventurer_count", _encounterAdventurers.Count },
			{ "monster_count", _encounterMonsters.Count }
		});

		foreach (CombatActionRunner runner in _runners)
		{
			runner.AdvanceTickCounters(tick);
		}

		List<QueuedCombatAction> queuedActions = new();

		foreach (CombatActionRunner runner in _runners)
		{
			AddIfQueued(queuedActions, runner.QueueActionForTick(tick));
		}

		foreach (RolledCombatAction rolledAction in RollActionOrder(queuedActions, tick))
		{
			rolledAction.QueuedAction.Runner.ResolveQueuedAction(rolledAction.QueuedAction, tick);
		}

		HandleEndConditions();
		PublishEncounterState();
		EmitBridgeEvent("combat_tick_completed", new GDict
		{
			{ "source", nameof(AdventurerCombatController) },
			{ "encounter_id", _encounterId },
			{ "tick", tick },
			{ "queued_action_count", queuedActions.Count },
			{ "adventurer_alive", _encounterAdventurers.FirstOrDefault()?.IsAlive ?? false },
			{ "monster_alive", _encounterMonsters.FirstOrDefault()?.IsAlive ?? false },
			{ "living_adventurer_count", _encounterAdventurers.Count(adventurer => adventurer.IsAlive) },
			{ "living_monster_count", _encounterMonsters.Count(monster => monster.IsAlive) }
		});
	}

	public void StopCombat()
	{
		foreach (CombatActionRunner runner in _runners)
		{
			runner.Stop();
		}

		_encounterActive = false;
		PublishEncounterState();
		_runners.Clear();
		_encounterAdventurers.Clear();
		_encounterMonsters.Clear();
	}

	public Monster? GetCurrentMonsterTarget(Adventurer adventurer)
	{
		return GetRunner(adventurer)?.Target as Monster
			?? _encounterMonsters.FirstOrDefault(monster => monster.IsAlive);
	}

	private bool HandleEndConditions()
	{
		if (!_encounterActive)
		{
			return true;
		}

		foreach (Adventurer adventurer in _encounterAdventurers.Where(adventurer => !adventurer.IsAlive))
		{
			if (_deadAdventurerEventIds.Add(adventurer.CombatantId))
			{
				EmitBridgeEvent("adventurer_died", new GDict
				{
					{ "source", nameof(AdventurerCombatController) },
					{ "encounter_id", _encounterId },
					{ "adventurer", adventurer.AdventurerName },
					{ "monster", _encounterMonsters.FirstOrDefault(monster => monster.IsAlive)?.MonsterName ?? "none" },
					{ "living_adventurer_count", _encounterAdventurers.Count(candidate => candidate.IsAlive) }
				});
			}
		}

		foreach (Monster monster in _encounterMonsters.Where(monster => !monster.IsAlive))
		{
			if (_defeatedMonsterEventIds.Add(monster.CombatantId))
			{
				GD.Print($"MONSTER_DEFEATED monster={monster.MonsterName}");
				EmitBridgeEvent("monster_defeated", new GDict
				{
					{ "source", nameof(AdventurerCombatController) },
					{ "encounter_id", _encounterId },
					{ "adventurer", _encounterAdventurers.FirstOrDefault(adventurer => adventurer.IsAlive)?.AdventurerName ?? _encounterAdventurers.FirstOrDefault()?.AdventurerName ?? "none" },
					{ "monster", monster.MonsterName },
					{ "gold_reward", monster.GoldReward },
					{ "experience_reward", monster.ExperienceReward },
					{ "living_monster_count", _encounterMonsters.Count(candidate => candidate.IsAlive) }
				});
				monster.PublishState();
			}
		}

		bool anyAdventurerAlive = _encounterAdventurers.Any(adventurer => adventurer.IsAlive);
		bool anyMonsterAlive = _encounterMonsters.Any(monster => monster.IsAlive);

		if (anyAdventurerAlive && anyMonsterAlive)
		{
			return false;
		}

		foreach (CombatActionRunner runner in _runners)
		{
			runner.Stop();
		}

		_encounterActive = false;
		return true;
	}

	public static IReadOnlyList<CombatAction> CreateAdventurerActions()
	{
		return new[]
		{
			CreateHeavyStrike(),
			CreateSpark(),
			CombatAction.BasicAttack()
		};
	}

	public static IReadOnlyList<CombatAction> CreateAdventurerActions(Adventurer adventurer)
	{
		return adventurer.Archetype switch
		{
			AdventurerArchetype.Mage => new[]
			{
				CreateSpark(),
				CombatAction.BasicAttack(160.0)
			},
			_ => new[]
			{
				CreateHeavyStrike(),
				CombatAction.BasicAttack()
			}
		};
	}

	private static CombatAction CreateHeavyStrike()
	{
		return new CombatAction(
			"heavy_strike",
			"Heavy Strike",
			CombatActionKind.Skill,
			48.0,
			12,
			0,
			1,
			true,
			false,
			10,
			1.5,
			false);
	}

	private static CombatAction CreateSpark()
	{
		return new CombatAction(
			"spark",
			"Spark",
			CombatActionKind.Spell,
			160.0,
			8,
			8,
			0,
			true,
			false,
			4,
			1.2,
			false);
	}

	private IEnumerable<RolledCombatAction> RollActionOrder(IReadOnlyList<QueuedCombatAction> queuedActions, long tick)
	{
		List<RolledCombatAction> rolledActions = new();

		foreach (QueuedCombatAction queuedAction in queuedActions)
		{
			int randomRoll = _rng.RandiRange(1, 100);
			CombatStats stats = queuedAction.Actor.Stats;
			int initiativeScore =
				randomRoll
				+ stats.Initiative
				+ GetAttackSpeedInitiativeBonus(stats.AttackSpeedTicks)
				- queuedAction.Action.ActionWeight;
			RolledCombatAction rolledAction = new(queuedAction, randomRoll, initiativeScore);
			rolledActions.Add(rolledAction);
			EmitBridgeEvent("combat_action_order_rolled", new GDict
			{
				{ "source", nameof(AdventurerCombatController) },
				{ "encounter_id", _encounterId },
				{ "tick", tick },
				{ "combatant", queuedAction.Actor.DisplayName },
				{ "combatant_kind", queuedAction.Actor.CombatantKind },
				{ "target", queuedAction.Target?.DisplayName ?? "none" },
				{ "action_id", queuedAction.Action.ActionId },
				{ "random_roll", randomRoll },
				{ "initiative", stats.Initiative },
				{ "attack_speed_ticks", stats.AttackSpeedTicks },
				{ "attack_speed_bonus", GetAttackSpeedInitiativeBonus(stats.AttackSpeedTicks) },
				{ "action_weight", queuedAction.Action.ActionWeight },
				{ "initiative_score", initiativeScore }
			});
		}

		return rolledActions
			.OrderByDescending(action => action.InitiativeScore)
			.ThenByDescending(action => action.QueuedAction.Actor.Initiative)
			.ThenBy(action => action.QueuedAction.Actor.AttackSpeed)
			.ThenBy(action => action.QueuedAction.Actor.CombatantId, StringComparer.Ordinal)
			.ToArray();
	}

	private static int GetAttackSpeedInitiativeBonus(int attackSpeedTicks)
	{
		return Math.Max(0, 12 - Math.Max(1, attackSpeedTicks));
	}

	private static void AddIfQueued(List<QueuedCombatAction> queuedActions, QueuedCombatAction? queuedAction)
	{
		if (queuedAction is not null)
		{
			queuedActions.Add(queuedAction);
		}
	}

	private static IReadOnlyList<CombatAction> CreateMonsterActions()
	{
		return new[]
		{
			CombatAction.BasicAttack()
		};
	}

	private Monster? SelectMonsterTarget(Adventurer adventurer)
	{
		return FindNearestLivingCombatant(adventurer, _encounterMonsters);
	}

	private Adventurer? SelectAdventurerTarget(Monster monster)
	{
		if (monster.AggroTarget is Adventurer aggroTarget
			&& aggroTarget.IsAlive
			&& _encounterAdventurers.Any(adventurer => ReferenceEquals(adventurer, aggroTarget)))
		{
			return aggroTarget;
		}

		return FindNearestLivingCombatant(monster, _encounterAdventurers);
	}

	private static TCombatant? FindNearestLivingCombatant<TCombatant>(ICombatant actor, IEnumerable<TCombatant> candidates)
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

	private CombatActionRunner? GetRunner(ICombatant? combatant)
	{
		if (combatant is null)
		{
			return null;
		}

		return _runners.FirstOrDefault(runner => ReferenceEquals(runner.Owner, combatant));
	}

	private void PublishEncounterState()
	{
		if (TestBridge.Instance?.IsActive != true)
		{
			return;
		}

		CombatActionRunner? primaryAdventurerRunner = GetRunner(_adventurer)
			?? _runners.FirstOrDefault(runner => runner.Owner is Adventurer);
		CombatActionRunner? primaryMonsterRunner = _runners.FirstOrDefault(runner => runner.Owner is Monster);
		Adventurer? primaryAdventurer = _adventurer ?? _encounterAdventurers.FirstOrDefault();
		Monster? primaryMonster = _encounterMonsters.FirstOrDefault();

		TestBridge.Instance.EmitState("combat_encounter", new GDict
		{
			{ "source", nameof(AdventurerCombatController) },
			{ "encounter_id", _encounterId },
			{ "active", _encounterActive },
			{ "last_processed_tick", _lastProcessedTick },
			{ "tick_interval_seconds", _tickIntervalSeconds },
			{ "adventurer", BuildRunnerState(primaryAdventurerRunner, primaryAdventurer) },
			{ "monster", BuildRunnerState(primaryMonsterRunner, primaryMonster) },
			{ "adventurers", BuildRunnerStates(_encounterAdventurers) },
			{ "monsters", BuildRunnerStates(_encounterMonsters) },
			{ "adventurer_count", _encounterAdventurers.Count },
			{ "monster_count", _encounterMonsters.Count },
			{ "living_adventurer_count", _encounterAdventurers.Count(adventurer => adventurer.IsAlive) },
			{ "living_monster_count", _encounterMonsters.Count(monster => monster.IsAlive) }
		});
	}

	private GArray BuildRunnerStates<TCombatant>(IEnumerable<TCombatant> combatants)
		where TCombatant : class, ICombatant
	{
		GArray states = new();

		foreach (TCombatant combatant in combatants)
		{
			states.Add(BuildRunnerState(GetRunner(combatant), combatant));
		}

		return states;
	}

	private static GDict BuildRunnerState(CombatActionRunner? runner, ICombatant? combatant)
	{
		GDict skillCooldowns = new();

		if (runner is not null)
		{
			foreach ((string key, int value) in runner.SkillCooldowns)
			{
				skillCooldowns[key] = value;
			}
		}

		return new GDict
		{
			{ "name", combatant?.DisplayName ?? "none" },
			{ "kind", combatant?.CombatantKind ?? "none" },
			{ "state", GetCombatantState(runner, combatant) },
			{ "target", runner?.Target?.DisplayName ?? "none" },
			{ "active_action", runner?.ActiveActionId ?? string.Empty },
			{ "queued_action", runner?.QueuedActionId ?? string.Empty },
			{ "last_action", combatant is Adventurer adventurer4 ? adventurer4.LastActionId : combatant is Monster monster4 ? monster4.LastActionId : string.Empty },
			{ "basic_attack_cooldown_ticks_remaining", runner?.BasicAttackCooldownTicksRemaining ?? 0 },
			{ "global_cooldown_ticks_remaining", combatant is Adventurer adventurer3 ? adventurer3.GlobalCooldownTicksRemaining : combatant is Monster monster3 ? monster3.GlobalCooldownTicksRemaining : 0 },
			{ "cast_ticks_remaining", combatant is Adventurer adventurer ? adventurer.CastTicksRemaining : combatant is Monster monster ? monster.CastTicksRemaining : 0 },
			{ "recovery_ticks_remaining", combatant is Adventurer adventurer2 ? adventurer2.RecoveryTicksRemaining : combatant is Monster monster2 ? monster2.RecoveryTicksRemaining : 0 },
			{ "skill_cooldowns", skillCooldowns },
			{ "is_alive", combatant?.IsAlive ?? false },
			{ "attack_speed", combatant?.AttackSpeed ?? 0 },
			{ "attack_speed_ticks", combatant?.AttackSpeed ?? 0 },
			{ "health", combatant?.Health ?? 0 },
			{ "max_health", combatant?.MaxHealth ?? 0 }
		};
	}

	private static string GetCombatantState(CombatActionRunner? runner, ICombatant? combatant)
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

	private static GArray BuildCombatantNames<TCombatant>(IEnumerable<TCombatant> combatants)
		where TCombatant : ICombatant
	{
		GArray names = new();

		foreach (TCombatant combatant in combatants)
		{
			names.Add(combatant.DisplayName);
		}

		return names;
	}

	private static void EmitBridgeEvent(string type, GDict payload)
	{
		TestBridge.Instance?.EmitEvent(type, payload);
	}
}
