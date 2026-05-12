#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using GDict = Godot.Collections.Dictionary;

public partial class Monster : Node2D, ICombatant
{
	[Export]
	public string MonsterName { get; set; } = "Slime";

	[Export]
	public int Level { get; set; } = 1;

	[Export]
	public int MaxHealth { get; set; } = 23;

	[Export]
	public int Attack { get; set; } = 6;

	[Export]
	public double Accuracy { get; set; } = 0.35;

	[Export]
	public int Defense { get; set; } = 1;

	[Export]
	public double Evasion { get; set; } = 0.0;

	[Export]
	public int Initiative { get; set; } = 1;

	[Export]
	public int AttackSpeed { get; set; } = 8;

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
	public CombatStats Stats => new(Attack, Accuracy, Defense, Evasion, Initiative, AttackSpeed, MaxHealth, Health);
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

	public override void _Ready()
	{
		if (!_hasSetup)
		{
			Health = MaxHealth;
		}

		PublishState();
	}

	public CombatStats CreateStartingStats()
	{
		return new CombatStats(Attack, Accuracy, Defense, Evasion, Initiative, AttackSpeed, MaxHealth, MaxHealth);
	}

	public void Setup(
		string? monsterName = null,
		int? level = null,
		CombatStats? stats = null,
		Vector2? position = null,
		int? goldReward = null,
		int? experienceReward = null)
	{
		MonsterName = monsterName ?? MonsterName;
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
			throw new InvalidOperationException($"{nameof(Monster)}.{nameof(Setup)} requires character stats.");
		}

		if (position is Vector2 setupPosition)
		{
			Position = setupPosition;
		}

		GoldReward = goldReward ?? GoldReward;
		ExperienceReward = experienceReward ?? ExperienceReward;
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
		QueueRedraw();

		if (IsInsideTree())
		{
			PublishState();
		}
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
			{ "initiative", Initiative },
			{ "attack_speed", AttackSpeed },
			{ "gold_reward", GoldReward },
			{ "experience_reward", ExperienceReward },
			{ "is_alive", IsAlive },
			{ "combat_state", CombatState.ToString() },
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
			{ "position", BridgePayload.VectorToArray(GlobalPosition) }
		});
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

	public override void _Draw()
	{
		Color bodyColor = IsAlive ? new Color(0.71f, 0.20f, 0.23f) : new Color(0.25f, 0.25f, 0.25f);
		DrawCircle(Vector2.Zero, 18.0f, bodyColor);
		DrawArc(Vector2.Zero, 24.0f, 0.0f, Mathf.Tau, 32, new Color(0.12f, 0.08f, 0.09f), 2.0f);
		DrawHealthBar(new Vector2(-24.0f, -34.0f), new Vector2(48.0f, 6.0f));
	}

	private void DrawHealthBar(Vector2 position, Vector2 size)
	{
		float healthRatio = MaxHealth <= 0 ? 0.0f : Mathf.Clamp((float)Health / MaxHealth, 0.0f, 1.0f);
		Color fillColor = healthRatio > 0.5f
			? new Color(0.22f, 0.78f, 0.34f)
			: healthRatio > 0.25f
				? new Color(0.95f, 0.72f, 0.20f)
				: new Color(0.90f, 0.24f, 0.24f);

		DrawRect(new Rect2(position, size), new Color(0.08f, 0.08f, 0.09f));
		DrawRect(new Rect2(position, new Vector2(size.X * healthRatio, size.Y)), fillColor);
		DrawRect(new Rect2(position, size), new Color(0.02f, 0.02f, 0.025f), false, 1.0f);
	}
}
