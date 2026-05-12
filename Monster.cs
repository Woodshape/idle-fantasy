#nullable enable

using Godot;
using System.Collections.Generic;
using GDict = Godot.Collections.Dictionary;

public partial class Monster : Node2D, ICombatant
{
	[Export]
	public string MonsterName { get; set; } = "Slime";

	[Export]
	public int Level { get; set; } = 1;

	[Export]
	public int MaxHealth { get; set; } = 18;

	[Export]
	public int Attack { get; set; } = 3;

	[Export]
	public double Accuracy { get; set; } = 0.0;

	[Export]
	public int Defense { get; set; } = 1;

	[Export]
	public double Evasion { get; set; } = 0.0;

	[Export]
	public double AttackSpeed { get; set; } = 0.65;

	[Export]
	public int GoldReward { get; set; } = 7;

	[Export]
	public int ExperienceReward { get; set; } = 10;

	public int Health { get; private set; }
	public CombatState CombatState { get; private set; } = CombatState.OutOfCombat;
	public bool IsAlive => Health > 0;
	public string CombatantId => $"monster:{MonsterName}";
	public string CombatantKind => "monster";
	public string DisplayName => MonsterName;
	public CombatStats Stats => new(Attack, Accuracy, Defense, Evasion, AttackSpeed, MaxHealth, Health);
	public string CurrentCombatTargetName { get; private set; } = string.Empty;
	public string QueuedActionId { get; private set; } = string.Empty;
	public string ActiveActionId { get; private set; } = string.Empty;
	public double BasicAttackCooldownRemaining { get; private set; }
	public double CastRemaining { get; private set; }
	public double RecoveryRemaining { get; private set; }
	public IReadOnlyDictionary<string, double> SkillCooldowns => _skillCooldowns;
	public bool IsDisabled { get; private set; }
	public bool CanAct { get; private set; }

	private readonly Dictionary<string, double> _skillCooldowns = new();

	public override void _Ready()
	{
		Health = MaxHealth;
		PublishState();
	}

	public int ApplyDamage(int amount)
	{
		if (!IsAlive)
		{
			return 0;
		}

		int previousHealth = Health;
		Health = Mathf.Max(0, Health - Mathf.Max(0, amount));

		if (Health <= 0)
		{
			CombatState = global::CombatState.Defeated;
		}

		QueueRedraw();
		PublishState();
		return previousHealth - Health;
	}

	public void SetCombatSnapshot(CombatantCombatSnapshot snapshot)
	{
		CombatState = !IsAlive ? global::CombatState.Defeated : snapshot.State;
		CurrentCombatTargetName = snapshot.CurrentTargetName;
		QueuedActionId = snapshot.QueuedActionId;
		ActiveActionId = snapshot.ActiveActionId;
		BasicAttackCooldownRemaining = snapshot.BasicAttackCooldownRemaining;
		CastRemaining = snapshot.CastRemaining;
		RecoveryRemaining = snapshot.RecoveryRemaining;
		IsDisabled = snapshot.IsDisabled;
		CanAct = snapshot.CanAct;
		_skillCooldowns.Clear();

		foreach ((string key, double value) in snapshot.SkillCooldowns)
		{
			_skillCooldowns[key] = value;
		}
	}

	public void ResetForNextHunt()
	{
		Health = MaxHealth;
		SetCombatSnapshot(new CombatantCombatSnapshot
		{
			State = global::CombatState.OutOfCombat
		});
		QueueRedraw();
		GD.Print($"MONSTER_RESPAWNED monster={MonsterName}");
		PublishState();
	}

	public void PublishState()
	{
		if (TestBridge.Instance?.IsActive != true)
		{
			return;
		}

		TestBridge.Instance.EmitState("monster", new GDict
		{
			{ "source", nameof(Monster) },
			{ "name", MonsterName },
			{ "level", Level },
			{ "health", Health },
			{ "max_health", MaxHealth },
			{ "attack", Attack },
			{ "accuracy", Accuracy },
			{ "defense", Defense },
			{ "evasion", Evasion },
			{ "attack_speed", AttackSpeed },
			{ "gold_reward", GoldReward },
			{ "experience_reward", ExperienceReward },
			{ "is_alive", IsAlive },
			{ "combat_state", CombatState.ToString() },
			{ "current_combat_target", CurrentCombatTargetName },
			{ "queued_action", QueuedActionId },
			{ "active_action", ActiveActionId },
			{ "basic_attack_cooldown_remaining", BasicAttackCooldownRemaining },
			{ "cast_remaining", CastRemaining },
			{ "recovery_remaining", RecoveryRemaining },
			{ "skill_cooldowns", BuildSkillCooldownState() },
			{ "is_disabled", IsDisabled },
			{ "can_act", CanAct },
			{ "position", BridgePayload.VectorToArray(GlobalPosition) }
		});
	}

	private GDict BuildSkillCooldownState()
	{
		GDict cooldowns = new();

		foreach ((string key, double value) in _skillCooldowns)
		{
			cooldowns[key] = value;
		}

		return cooldowns;
	}

	public override void _Draw()
	{
		Color bodyColor = IsAlive ? new Color(0.71f, 0.20f, 0.23f) : new Color(0.25f, 0.25f, 0.25f);
		DrawCircle(Vector2.Zero, 18.0f, bodyColor);
		DrawArc(Vector2.Zero, 24.0f, 0.0f, Mathf.Tau, 32, new Color(0.12f, 0.08f, 0.09f), 2.0f);
	}
}
