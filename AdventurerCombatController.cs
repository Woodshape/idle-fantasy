#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GDict = Godot.Collections.Dictionary;

public partial class AdventurerCombatController : Node
{
	private readonly RandomNumberGenerator _rng = new();
	private Adventurer? _adventurer;
	private Monster? _target;
	private CombatActionRunner? _adventurerRunner;
	private CombatActionRunner? _monsterRunner;
	private int _encounterId;
	private long _lastProcessedTick;
	private double _tickIntervalSeconds = 0.25;
	private bool _monsterDefeatedEmitted;
	private bool _adventurerDiedEmitted;

	public CombatState State => _adventurer?.CombatState ?? CombatState.OutOfCombat;
	public int AttackCooldownTicksRemaining => _adventurerRunner?.BasicAttackCooldownTicksRemaining ?? 0;

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

		_target = target;
		_encounterId++;
		_lastProcessedTick = currentTick;
		_monsterDefeatedEmitted = false;
		_adventurerDiedEmitted = false;
		_adventurerRunner = new CombatActionRunner(_adventurer, CreateAdventurerActions(), _rng, EmitBridgeEvent);
		_monsterRunner = new CombatActionRunner(target, CreateMonsterActions(), _rng, EmitBridgeEvent);
		_adventurerRunner.Start(target, currentTick);
		_monsterRunner.Start(_adventurer, currentTick);

		GD.Print($"COMBAT_STARTED tick={currentTick} encounter={_encounterId} adventurer={_adventurer.AdventurerName} monster={target.MonsterName}");
		EmitBridgeEvent("combat_started", new GDict
		{
			{ "source", nameof(AdventurerCombatController) },
			{ "encounter_id", _encounterId },
			{ "tick", currentTick },
			{ "adventurer", _adventurer.AdventurerName },
			{ "monster", target.MonsterName },
			{ "adventurer_attack_speed_ticks", _adventurer.AttackSpeed },
			{ "monster_attack_speed_ticks", target.AttackSpeed },
			{ "tick_interval_seconds", _tickIntervalSeconds },
			{ "timing_model", "simulation_ticks" }
		});
		PublishEncounterState();
	}

	public void ProcessSimulationTick(long tick, double tickIntervalSeconds)
	{
		_tickIntervalSeconds = tickIntervalSeconds;
		_adventurer ??= GetParentOrNull<Adventurer>();

		if (_adventurer is null || _target is null || _adventurerRunner is null || _monsterRunner is null)
		{
			return;
		}

		_lastProcessedTick = tick;
		EmitBridgeEvent("combat_tick_started", new GDict
		{
			{ "source", nameof(AdventurerCombatController) },
			{ "encounter_id", _encounterId },
			{ "tick", tick },
			{ "tick_interval_seconds", tickIntervalSeconds }
		});

		_adventurerRunner.AdvanceTickCounters(tick);
		_monsterRunner.AdvanceTickCounters(tick);

		List<QueuedCombatAction> queuedActions = new();
		AddIfQueued(queuedActions, _adventurerRunner.QueueActionForTick(tick));
		AddIfQueued(queuedActions, _monsterRunner.QueueActionForTick(tick));

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
			{ "adventurer_alive", _adventurer.IsAlive },
			{ "monster_alive", _target?.IsAlive ?? false }
		});
	}

	public void StopCombat()
	{
		_adventurerRunner?.Stop();

		if (_target?.IsAlive == true)
		{
			_monsterRunner?.Stop();
		}

		_target = null;
		_adventurerRunner = null;
		_monsterRunner = null;
		PublishEncounterState();
	}

	private bool HandleEndConditions()
	{
		if (_adventurer is null)
		{
			return true;
		}

		if (!_adventurer.IsAlive)
		{
			if (!_adventurerDiedEmitted)
			{
				_adventurerDiedEmitted = true;
				EmitBridgeEvent("adventurer_died", new GDict
				{
					{ "source", nameof(AdventurerCombatController) },
					{ "encounter_id", _encounterId },
					{ "adventurer", _adventurer.AdventurerName },
					{ "monster", _target?.MonsterName ?? "none" }
				});
			}

			_adventurerRunner?.Stop();
			_monsterRunner?.Stop();
			_adventurerRunner = null;
			_monsterRunner = null;
			_target = null;
			return true;
		}

		if (_target is null)
		{
			return true;
		}

		if (!_target.IsAlive)
		{
			if (!_monsterDefeatedEmitted)
			{
				_monsterDefeatedEmitted = true;
				GD.Print($"MONSTER_DEFEATED monster={_target.MonsterName}");
				EmitBridgeEvent("monster_defeated", new GDict
				{
					{ "source", nameof(AdventurerCombatController) },
					{ "encounter_id", _encounterId },
					{ "adventurer", _adventurer.AdventurerName },
					{ "monster", _target.MonsterName },
					{ "gold_reward", _target.GoldReward },
					{ "experience_reward", _target.ExperienceReward }
				});
			}

			_adventurerRunner?.Stop();
			_monsterRunner?.Stop();
			_target.PublishState();
			_adventurerRunner = null;
			_monsterRunner = null;
			_target = null;
			return true;
		}

		return false;
	}

	private static IReadOnlyList<CombatAction> CreateAdventurerActions()
	{
		return new[]
		{
			new CombatAction(
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
				false),
			CombatAction.BasicAttack()
		};
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
			.ThenBy(action => action.QueuedAction.Actor.CombatantId, System.StringComparer.Ordinal)
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

	private void PublishEncounterState()
	{
		if (TestBridge.Instance?.IsActive != true)
		{
			return;
		}

		TestBridge.Instance.EmitState("combat_encounter", new GDict
		{
			{ "source", nameof(AdventurerCombatController) },
			{ "encounter_id", _encounterId },
			{ "active", _target is not null && _adventurer?.CombatState != CombatState.OutOfCombat },
			{ "last_processed_tick", _lastProcessedTick },
			{ "tick_interval_seconds", _tickIntervalSeconds },
			{ "adventurer", BuildRunnerState(_adventurerRunner, _adventurer) },
			{ "monster", BuildRunnerState(_monsterRunner, _monsterRunner?.Owner) }
		});
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

	private static void EmitBridgeEvent(string type, GDict payload)
	{
		TestBridge.Instance?.EmitEvent(type, payload);
	}
}
