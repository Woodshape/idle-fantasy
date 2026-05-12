#nullable enable

using Godot;
using System.Collections.Generic;
using GDict = Godot.Collections.Dictionary;

public partial class AdventurerCombatController : Node
{
	private readonly RandomNumberGenerator _rng = new();
	private Adventurer? _adventurer;
	private Monster? _target;
	private CombatActionRunner? _adventurerRunner;
	private CombatActionRunner? _monsterRunner;
	private int _encounterId;
	private bool _monsterDefeatedEmitted;
	private bool _adventurerDiedEmitted;

	public CombatState State => _adventurer?.CombatState ?? CombatState.OutOfCombat;
	public double AttackCooldownRemaining => _adventurerRunner?.BasicAttackCooldownRemaining ?? 0.0;

	public override void _Ready()
	{
		_rng.Randomize();
		_adventurer = GetParentOrNull<Adventurer>();
	}

	public override void _PhysicsProcess(double delta)
	{
		_adventurer ??= GetParentOrNull<Adventurer>();

		if (_adventurer is null || _target is null)
		{
			return;
		}

		_adventurerRunner?.Update(delta);

		if (HandleEndConditions())
		{
			PublishEncounterState();
			return;
		}

		_monsterRunner?.Update(delta);
		HandleEndConditions();
		PublishEncounterState();
	}

	public void StartCombat(Monster target)
	{
		_adventurer ??= GetParentOrNull<Adventurer>();

		if (_adventurer is null)
		{
			return;
		}

		_target = target;
		_encounterId++;
		_monsterDefeatedEmitted = false;
		_adventurerDiedEmitted = false;
		_adventurerRunner = new CombatActionRunner(_adventurer, CreateAdventurerActions(), _rng, EmitBridgeEvent);
		_monsterRunner = new CombatActionRunner(target, CreateMonsterActions(), _rng, EmitBridgeEvent);
		_adventurerRunner.Start(target);
		_monsterRunner.Start(_adventurer);

		GD.Print($"COMBAT_STARTED encounter={_encounterId} adventurer={_adventurer.AdventurerName} monster={target.MonsterName}");
		EmitBridgeEvent("combat_started", new GDict
		{
			{ "source", nameof(AdventurerCombatController) },
			{ "encounter_id", _encounterId },
			{ "adventurer", _adventurer.AdventurerName },
			{ "monster", target.MonsterName },
			{ "adventurer_attacks_per_second", _adventurer.AttackSpeed },
			{ "monster_attacks_per_second", target.AttackSpeed },
			{ "timing_model", "per_combatant_action_timers" }
		});
		PublishEncounterState();
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
			_adventurerRunner?.Update(0.0);

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
			_monsterRunner?.Update(0.0);

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
				1.0,
				0.0,
				0.25,
				true,
				false,
				1.5,
				false),
			CombatAction.BasicAttack()
		};
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
			{ "adventurer", BuildRunnerState(_adventurerRunner, _adventurer) },
			{ "monster", BuildRunnerState(_monsterRunner, _monsterRunner?.Owner) }
		});
	}

	private static GDict BuildRunnerState(CombatActionRunner? runner, ICombatant? combatant)
	{
		GDict skillCooldowns = new();

		if (runner is not null)
		{
			foreach ((string key, double value) in runner.SkillCooldowns)
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
			{ "basic_attack_cooldown_remaining", runner?.BasicAttackCooldownRemaining ?? 0.0 },
			{ "cast_remaining", combatant is Adventurer adventurer ? adventurer.CastRemaining : combatant is Monster monster ? monster.CastRemaining : 0.0 },
			{ "recovery_remaining", combatant is Adventurer adventurer2 ? adventurer2.RecoveryRemaining : combatant is Monster monster2 ? monster2.RecoveryRemaining : 0.0 },
			{ "skill_cooldowns", skillCooldowns },
			{ "is_alive", combatant?.IsAlive ?? false },
			{ "attack_speed", combatant?.AttackSpeed ?? 0.0 },
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
