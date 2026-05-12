#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using GDict = Godot.Collections.Dictionary;

public partial class Adventurer : Node2D, ICombatant
{
	[Export]
	public string AdventurerName { get; set; } = "Mira";

	[Export]
	public int Level { get; set; } = 1;

	[Export]
	public int MaxHealth { get; set; } = 30;

	[Export]
	public int Attack { get; set; } = 7;

	[Export]
	public double Accuracy { get; set; } = 0.20;

	[Export]
	public int Defense { get; set; } = 1;

	[Export]
	public double Evasion { get; set; } = 0.15;

	[Export]
	public int Initiative { get; set; } = 4;

	[Export]
	public int AttackSpeed { get; set; } = 4;

	[Export]
	public float Speed { get; set; } = 120.0f;

	[Export]
	public float StopDistance { get; set; } = 8.0f;

	public int Experience { get; private set; }
	public int Gold { get; private set; }
	public int Health { get; private set; }
	public Vector2? MoveTarget { get; private set; }
	public Monster? CurrentMonsterTarget { get; private set; }
	public AdventurerController? Controller { get; private set; }
	public AdventurerCombatController? CombatController { get; private set; }
	public bool IsAlive => Health > 0;
	public string IntentionStateName => Controller?.State.ToString() ?? "Unknown";
	public string CombatStateName => CombatState.ToString();
	public string CombatantId => "adventurer";
	public string CombatantKind => "adventurer";
	public string DisplayName => AdventurerName;
	public CombatStats Stats => new(Attack, Accuracy, Defense, Evasion, Initiative, AttackSpeed, MaxHealth, Health);
	public CombatState CombatState { get; private set; } = CombatState.OutOfCombat;
	public string CurrentCombatTargetName { get; private set; } = string.Empty;
	public string QueuedActionId { get; private set; } = string.Empty;
	public string ActiveActionId { get; private set; } = string.Empty;
	public int BasicAttackCooldownTicksRemaining { get; private set; }
	public int GlobalCooldownTicksRemaining { get; private set; }
	public int CastTicksRemaining { get; private set; }
	public int RecoveryTicksRemaining { get; private set; }
	public IReadOnlyDictionary<string, int> SkillCooldowns => _skillCooldowns;
	public bool IsDisabled { get; private set; }
	public bool CanAct { get; private set; }

	private readonly Dictionary<string, int> _skillCooldowns = new();
	private bool _hasSetup;
	private ProgressBar? _healthBar;

	public override void _Ready()
	{
		if (!_hasSetup)
		{
			Health = MaxHealth;
		}

		Controller = GetNodeOrNull<AdventurerController>("AdventurerController");
		CombatController = GetNodeOrNull<AdventurerCombatController>("AdventurerCombatController");
		_healthBar = GetNodeOrNull<ProgressBar>("HealthBar");
		UpdateHealthBar();
		PublishState();
	}

	public CombatStats CreateStartingStats()
	{
		return new CombatStats(Attack, Accuracy, Defense, Evasion, Initiative, AttackSpeed, MaxHealth, MaxHealth);
	}

	public void Setup(
		string? adventurerName = null,
		int? level = null,
		CombatStats? stats = null,
		Vector2? position = null,
		float? speed = null,
		float? stopDistance = null,
		int experience = 0,
		int gold = 0)
	{
		AdventurerName = adventurerName ?? AdventurerName;
		Level = level ?? Level;

		if (stats is CombatStats setupStats)
		{
			Attack = setupStats.Attack;
			Accuracy = setupStats.Accuracy;
			Defense = setupStats.Defense;
			Evasion = setupStats.Evasion;
			Initiative = setupStats.Initiative;
			AttackSpeed = setupStats.AttackSpeedTicks;
			MaxHealth = setupStats.MaxHealth;
			Health = Mathf.Clamp(setupStats.CurrentHealth, 0, MaxHealth);
		}
		else
		{
			throw new InvalidOperationException($"{nameof(Adventurer)}.{nameof(Setup)} requires character stats.");
		}

		if (position is Vector2 setupPosition)
		{
			Position = setupPosition;
		}

		Speed = speed ?? Speed;
		StopDistance = stopDistance ?? StopDistance;
		Experience = experience;
		Gold = gold;
		MoveTarget = null;
		CurrentMonsterTarget = null;
		CombatState = Health > 0 ? global::CombatState.OutOfCombat : global::CombatState.Defeated;
		CurrentCombatTargetName = string.Empty;
		QueuedActionId = string.Empty;
		ActiveActionId = string.Empty;
		BasicAttackCooldownTicksRemaining = 0;
		GlobalCooldownTicksRemaining = 0;
		CastTicksRemaining = 0;
		RecoveryTicksRemaining = 0;
		IsDisabled = false;
		CanAct = false;
		_skillCooldowns.Clear();
		_hasSetup = true;
		UpdateHealthBar();

		if (IsInsideTree())
		{
			PublishState();
		}
	}

	public void SetMoveTarget(Vector2 target)
	{
		MoveTarget = target;
		PublishState();
	}

	public void SetCombatTarget(Monster monster)
	{
		CurrentMonsterTarget = monster;
		PublishState();
	}

	public void ClearCombatTarget()
	{
		CurrentMonsterTarget = null;
		PublishState();
	}

	public bool MoveTowardTarget(double delta)
	{
		if (MoveTarget is not Vector2 target)
		{
			return true;
		}

		Vector2 toTarget = target - GlobalPosition;
		float distance = toTarget.Length();

		if (distance <= StopDistance)
		{
			GlobalPosition = target;
			MoveTarget = null;
			PublishState();
			return true;
		}

		Vector2 movement = toTarget.Normalized() * Speed * (float)delta;
		GlobalPosition += movement.Length() >= distance ? toTarget : movement;
		PublishState();
		return false;
	}

	public int ApplyDamage(int amount)
	{
		if (!IsAlive)
		{
			return 0;
		}

		int previousHealth = Health;
		Health = Mathf.Max(0, Health - Mathf.Max(0, amount));
		UpdateHealthBar();
		PublishState();
		return previousHealth - Health;
	}

	public void AddRewards(int gold, int experience)
	{
		Gold += gold;
		Experience += experience;
		PublishState();
	}

	public void RecoverToFull()
	{
		Health = MaxHealth;
		UpdateHealthBar();
		PublishState();
	}

	public void SetCombatSnapshot(CombatantCombatSnapshot snapshot)
	{
		CombatState = !IsAlive ? global::CombatState.Defeated : snapshot.State;
		CurrentCombatTargetName = snapshot.CurrentTargetName;
		QueuedActionId = snapshot.QueuedActionId;
		ActiveActionId = snapshot.ActiveActionId;
		BasicAttackCooldownTicksRemaining = snapshot.BasicAttackCooldownTicksRemaining;
		GlobalCooldownTicksRemaining = snapshot.GlobalCooldownTicksRemaining;
		CastTicksRemaining = snapshot.CastTicksRemaining;
		RecoveryTicksRemaining = snapshot.RecoveryTicksRemaining;
		IsDisabled = snapshot.IsDisabled;
		CanAct = snapshot.CanAct;
		_skillCooldowns.Clear();

		foreach ((string key, int value) in snapshot.SkillCooldowns)
		{
			_skillCooldowns[key] = value;
		}
	}

	public void PublishState()
	{
		if (TestBridge.Instance?.IsActive != true)
		{
			return;
		}

		GDict state = new()
		{
			{ "source", nameof(Adventurer) },
			{ "name", AdventurerName },
			{ "level", Level },
			{ "experience", Experience },
			{ "gold", Gold },
			{ "health", Health },
			{ "max_health", MaxHealth },
			{ "attack", Attack },
			{ "accuracy", Accuracy },
			{ "defense", Defense },
			{ "evasion", Evasion },
			{ "initiative", Initiative },
			{ "attack_speed", AttackSpeed },
			{ "speed", Speed },
			{ "is_alive", IsAlive },
			{ "intention_state", IntentionStateName },
			{ "combat_state", CombatStateName },
			{ "current_combat_target", CurrentCombatTargetName },
			{ "queued_action", QueuedActionId },
			{ "active_action", ActiveActionId },
			{ "basic_attack_cooldown_ticks_remaining", BasicAttackCooldownTicksRemaining },
			{ "global_cooldown_ticks_remaining", GlobalCooldownTicksRemaining },
			{ "cast_ticks_remaining", CastTicksRemaining },
			{ "recovery_ticks_remaining", RecoveryTicksRemaining },
			{ "skill_cooldowns", BuildSkillCooldownState() },
			{ "is_disabled", IsDisabled },
			{ "can_act", CanAct },
			{ "position", BridgePayload.VectorToArray(GlobalPosition) },
			{ "has_move_target", MoveTarget is not null }
		};

		if (MoveTarget is Vector2 moveTarget)
		{
			state["move_target"] = BridgePayload.VectorToArray(moveTarget);
		}

		if (CurrentMonsterTarget is not null)
		{
			state["target_monster"] = CurrentMonsterTarget.MonsterName;
		}

		TestBridge.Instance.EmitState("adventurer", state);
	}

	private void UpdateHealthBar()
	{
		if (_healthBar is null)
		{
			return;
		}

		_healthBar.MaxValue = Mathf.Max(1, MaxHealth);
		_healthBar.Value = Mathf.Clamp(Health, 0, MaxHealth);
	}

	private GDict BuildSkillCooldownState()
	{
		GDict cooldowns = new();

		foreach ((string key, int value) in _skillCooldowns)
		{
			cooldowns[key] = value;
		}

		return cooldowns;
	}
}
