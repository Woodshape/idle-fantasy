#nullable enable

using Godot;
using System;
using GDict = Godot.Collections.Dictionary;

public enum AdventurerCombatState
{
	OutOfCombat,
	Engaging,
	Ready,
	AttackCooldown,
	SkillCooldown,
	Casting,
	Recovering,
	Disabled,
	Defeated
}

public partial class AdventurerCombatController : Node
{
	private const double CombatTickInterval = 1.0;
	private const double BaseHitChance = 0.75;
	private const double MinHitChance = 0.05;
	private const double MaxHitChance = 0.95;

	private readonly RandomNumberGenerator _rng = new();
	private Adventurer? _adventurer;
	private Monster? _target;

	public AdventurerCombatState State { get; private set; } = AdventurerCombatState.OutOfCombat;
	public double AttackCooldownRemaining { get; private set; }

	public override void _Ready()
	{
		_rng.Randomize();
		_adventurer = GetParentOrNull<Adventurer>();
	}

	public override void _PhysicsProcess(double delta)
	{
		_adventurer ??= GetParentOrNull<Adventurer>();

		if (_adventurer is null || State is AdventurerCombatState.OutOfCombat or AdventurerCombatState.Defeated)
		{
			return;
		}

		if (!_adventurer.IsAlive)
		{
			ChangeState(AdventurerCombatState.Defeated);
			return;
		}

		if (_target is null || !_target.IsAlive)
		{
			StopCombat();
			return;
		}

		if (State == AdventurerCombatState.Engaging)
		{
			AttackCooldownRemaining = CombatTickInterval;
			ChangeState(AdventurerCombatState.AttackCooldown);
			return;
		}

		if (State == AdventurerCombatState.AttackCooldown)
		{
			AttackCooldownRemaining = Math.Max(0.0, AttackCooldownRemaining - delta);
			_adventurer.PublishState();

			if (AttackCooldownRemaining <= 0.0)
			{
				ChangeState(AdventurerCombatState.Ready);
			}

			return;
		}

		if (State == AdventurerCombatState.Ready)
		{
			ResolveCombatTick();
		}
	}

	public void StartCombat(Monster target)
	{
		if (_adventurer is null)
		{
			_adventurer = GetParentOrNull<Adventurer>();
		}

		_target = target;
		target.SetCombatState(AdventurerCombatState.Engaging);
		GD.Print($"COMBAT_STARTED adventurer={_adventurer?.AdventurerName} monster={target.MonsterName}");
		EmitBridgeEvent("combat_started", new GDict
		{
			{ "source", nameof(AdventurerCombatController) },
			{ "adventurer", _adventurer?.AdventurerName ?? "unknown" },
			{ "monster", target.MonsterName },
			{ "tick_interval", CombatTickInterval }
		});
		ChangeState(AdventurerCombatState.Engaging);
	}

	public void StopCombat()
	{
		_target?.SetCombatState(AdventurerCombatState.OutOfCombat);
		_target = null;
		AttackCooldownRemaining = 0.0;
		ChangeState(AdventurerCombatState.OutOfCombat);
	}

	private void ResolveCombatTick()
	{
		if (_adventurer is null || _target is null)
		{
			StopCombat();
			return;
		}

		ResolveAttack(
			_adventurer.AdventurerName,
			_target.MonsterName,
			_adventurer.Attack,
			_adventurer.Accuracy,
			_target.Defense,
			_target.Evasion,
			damage => _target.ApplyDamage(damage));

		if (!_target.IsAlive)
		{
			_target.SetCombatState(AdventurerCombatState.Defeated);
			GD.Print($"MONSTER_DEFEATED monster={_target.MonsterName}");
			EmitBridgeEvent("monster_defeated", new GDict
			{
				{ "source", nameof(AdventurerCombatController) },
				{ "adventurer", _adventurer.AdventurerName },
				{ "monster", _target.MonsterName },
				{ "gold_reward", _target.GoldReward },
				{ "experience_reward", _target.ExperienceReward }
			});
			StopCombat();
			return;
		}

		ResolveAttack(
			_target.MonsterName,
			_adventurer.AdventurerName,
			_target.Attack,
			_target.Accuracy,
			_adventurer.Defense,
			_adventurer.Evasion,
			damage => _adventurer.ApplyDamage(damage));

		if (!_adventurer.IsAlive)
		{
			ChangeState(AdventurerCombatState.Defeated);
			EmitBridgeEvent("adventurer_died", new GDict
			{
				{ "source", nameof(AdventurerCombatController) },
				{ "adventurer", _adventurer.AdventurerName },
				{ "monster", _target.MonsterName }
			});
			return;
		}

		AttackCooldownRemaining = CombatTickInterval;
		ChangeState(AdventurerCombatState.AttackCooldown);
	}

	private void ResolveAttack(
		string attackerName,
		string defenderName,
		int attackerAttack,
		double attackerAccuracy,
		int defenderDefense,
		double defenderEvasion,
		Func<int, int> applyDamage)
	{
		double hitChance = Math.Clamp(BaseHitChance + attackerAccuracy - defenderEvasion, MinHitChance, MaxHitChance);
		double roll = _rng.Randf();
		bool hit = roll <= hitChance;
		int damage = hit ? Math.Max(1, attackerAttack - defenderDefense) : 0;

		GD.Print($"ATTACK_ROLL attacker={attackerName} defender={defenderName} hit_chance={hitChance:0.00} roll={roll:0.00} hit={hit} damage={damage}");
		EmitBridgeEvent("attack_roll_resolved", new GDict
		{
			{ "source", nameof(AdventurerCombatController) },
			{ "attacker", attackerName },
			{ "defender", defenderName },
			{ "hit_chance", hitChance },
			{ "roll", roll },
			{ "hit", hit },
			{ "damage", damage }
		});

		if (!hit)
		{
			return;
		}

		int appliedDamage = applyDamage(damage);
		EmitBridgeEvent("damage_applied", new GDict
		{
			{ "source", nameof(AdventurerCombatController) },
			{ "attacker", attackerName },
			{ "defender", defenderName },
			{ "damage", appliedDamage }
		});
	}

	private void ChangeState(AdventurerCombatState nextState)
	{
		AdventurerCombatState previousState = State;

		if (previousState == nextState)
		{
			return;
		}

		State = nextState;
		GD.Print($"COMBAT_STATE from={previousState} to={nextState}");
		EmitBridgeEvent("combat_state_changed", new GDict
		{
			{ "source", nameof(AdventurerCombatController) },
			{ "from", previousState.ToString() },
			{ "to", nextState.ToString() },
			{ "adventurer", _adventurer?.AdventurerName ?? "unknown" },
			{ "monster", _target?.MonsterName ?? "none" }
		});
		_adventurer?.PublishState();
		_target?.PublishState();
	}

	private static void EmitBridgeEvent(string type, GDict payload)
	{
		TestBridge.Instance?.EmitEvent(type, payload);
	}
}
