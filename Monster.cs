#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
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

	[Export]
	public float Speed { get; set; } = 90.0f;

	[Export]
	public float AggroRange { get; set; } = 48.0f;

	[Export]
	public float AggroAttackDistance { get; set; } = 42.0f;

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
	public string LastActionId { get; private set; } = string.Empty;
	public string DefinitionId { get; private set; } = string.Empty;
	public string CombatLoadoutId { get; private set; } = string.Empty;
	public int BasicAttackCooldownTicksRemaining { get; private set; }
	public int GlobalCooldownTicksRemaining { get; private set; }
	public int CastTicksRemaining { get; private set; }
	public int RecoveryTicksRemaining { get; private set; }
	public IReadOnlyDictionary<string, int> SkillCooldowns => _skillCooldowns;
	public bool IsDisabled { get; private set; }
	public bool CanAct { get; private set; }
	public Adventurer? AggroTarget { get; private set; }
	public bool HasAggroTarget => AggroTarget?.IsAlive == true;

	private readonly Dictionary<string, int> _skillCooldowns = new();
	private bool _hasSetup;
	private ProgressBar? _healthBar;
	private bool _wasMovingToAggroTarget;
	private Vector2 _homePosition;

	public override void _Ready()
	{
		if (!_hasSetup)
		{
			Health = MaxHealth;
			_homePosition = Position;
		}

		_healthBar = GetNodeOrNull<ProgressBar>("HealthBar");
		UpdateHealthBar();
		PublishState();
	}

	public override void _PhysicsProcess(double delta)
	{
		UpdateAggroMovement(delta);
	}

	public void ProcessSimulationTick(GameController game, long currentTick)
	{
		UpdateProximityAggro(game, currentTick);
		TryJoinAggroEncounter(game, currentTick);
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
			_homePosition = setupPosition;
		}
		else
		{
			_homePosition = Position;
		}

		GoldReward = goldReward ?? GoldReward;
		ExperienceReward = experienceReward ?? ExperienceReward;
		AggroTarget = null;
		_wasMovingToAggroTarget = false;
		CombatState = Health > 0 ? global::CombatState.OutOfCombat : global::CombatState.Defeated;
		CurrentCombatTargetName = string.Empty;
		QueuedActionId = string.Empty;
		ActiveActionId = string.Empty;
		LastActionId = string.Empty;
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

		UpdateHealthBar();
		PublishState();
		return previousHealth - Health;
	}

	public void SetAggroTarget(Adventurer attacker, string actionId, string aggroTrigger, long tick)
	{
		if (!IsAlive || !attacker.IsAlive)
		{
			return;
		}

		bool changedTarget = !ReferenceEquals(AggroTarget, attacker);
		AggroTarget = attacker;

		if (changedTarget)
		{
			_wasMovingToAggroTarget = false;
			EmitBridgeEvent("monster_aggro_target_set", new GDict
			{
				{ "source", nameof(Monster) },
				{ "tick", tick },
				{ "monster", MonsterName },
				{ "target", attacker.AdventurerName },
				{ "action_id", actionId },
				{ "aggro_trigger", aggroTrigger },
				{ "distance_to_target", GlobalPosition.DistanceTo(attacker.GlobalPosition) },
				{ "aggro_range", AggroRange },
				{ "aggro_attack_distance", AggroAttackDistance }
			});
		}

		PublishState();
	}

	public void SetCombatSnapshot(CombatantCombatSnapshot snapshot)
	{
		CombatState = !IsAlive ? global::CombatState.Defeated : snapshot.State;
		CurrentCombatTargetName = snapshot.CurrentTargetName;
		QueuedActionId = snapshot.QueuedActionId;
		ActiveActionId = snapshot.ActiveActionId;
		LastActionId = snapshot.LastActionId;
		DefinitionId = snapshot.DefinitionId;
		CombatLoadoutId = snapshot.CombatLoadoutId;
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
		Position = _homePosition;
		AggroTarget = null;
		_wasMovingToAggroTarget = false;
		SetCombatSnapshot(new CombatantCombatSnapshot
		{
			State = global::CombatState.OutOfCombat
		});
		UpdateHealthBar();
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
			{ "speed", Speed },
			{ "aggro_range", AggroRange },
			{ "aggro_attack_distance", AggroAttackDistance },
			{ "gold_reward", GoldReward },
			{ "experience_reward", ExperienceReward },
			{ "is_alive", IsAlive },
			{ "combat_state", CombatState.ToString() },
			{ "current_combat_target", CurrentCombatTargetName },
			{ "queued_action", QueuedActionId },
			{ "active_action", ActiveActionId },
			{ "last_action", LastActionId },
			{ "definition_id", DefinitionId },
			{ "combat_loadout_id", CombatLoadoutId },
			{ "basic_attack_cooldown_ticks_remaining", BasicAttackCooldownTicksRemaining },
			{ "global_cooldown_ticks_remaining", GlobalCooldownTicksRemaining },
			{ "cast_ticks_remaining", CastTicksRemaining },
			{ "recovery_ticks_remaining", RecoveryTicksRemaining },
			{ "skill_cooldowns", BuildSkillCooldownState() },
			{ "is_disabled", IsDisabled },
			{ "can_act", CanAct },
			{ "has_aggro_target", HasAggroTarget },
			{ "aggro_target", AggroTarget?.AdventurerName ?? string.Empty },
			{ "position", BridgePayload.VectorToArray(GlobalPosition) }
		});
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

	private void UpdateProximityAggro(GameController game, long currentTick)
	{
		if (!IsAlive || HasAggroTarget)
		{
			return;
		}

		Adventurer? adventurer = game.Adventurers
			.Where(candidate => candidate.IsAlive)
			.OrderBy(candidate => candidate.GlobalPosition.DistanceSquaredTo(GlobalPosition))
			.FirstOrDefault()
			?? game.Adventurer;

		if (adventurer?.IsAlive != true)
		{
			return;
		}

		float distance = GlobalPosition.DistanceTo(adventurer.GlobalPosition);

		if (distance > AggroRange)
		{
			return;
		}

		SetAggroTarget(adventurer, string.Empty, "proximity", currentTick);
	}

	private void TryJoinAggroEncounter(GameController game, long currentTick)
	{
		if (AggroTarget is Adventurer target && target.IsAlive)
		{
			game.TryAddAggroMonsterToEncounter(this, target, "proximity", currentTick);
		}
	}

	private void UpdateAggroMovement(double delta)
	{
		if (!IsAlive)
		{
			ClearAggroTarget();
			return;
		}

		if (AggroTarget is not Adventurer target || !target.IsAlive)
		{
			ClearAggroTarget();
			return;
		}

		Vector2 toTarget = target.GlobalPosition - GlobalPosition;
		float distance = toTarget.Length();

		if (distance <= AggroAttackDistance)
		{
			if (_wasMovingToAggroTarget)
			{
				_wasMovingToAggroTarget = false;
				EmitBridgeEvent("monster_aggro_arrived", new GDict
				{
					{ "source", nameof(Monster) },
					{ "monster", MonsterName },
					{ "target", target.AdventurerName },
					{ "distance_to_target", distance },
					{ "aggro_attack_distance", AggroAttackDistance }
				});
			}

			PublishState();
			return;
		}

		if (!_wasMovingToAggroTarget)
		{
			_wasMovingToAggroTarget = true;
			EmitBridgeEvent("monster_aggro_moving", new GDict
			{
				{ "source", nameof(Monster) },
				{ "monster", MonsterName },
				{ "target", target.AdventurerName },
				{ "distance_to_target", distance },
				{ "aggro_attack_distance", AggroAttackDistance }
			});
		}

		Vector2 movement = toTarget.Normalized() * Speed * (float)delta;
		GlobalPosition += movement.Length() >= distance - AggroAttackDistance
			? toTarget.Normalized() * Mathf.Max(0.0f, distance - AggroAttackDistance)
			: movement;
		PublishState();
	}

	private void ClearAggroTarget()
	{
		if (AggroTarget is null && !_wasMovingToAggroTarget)
		{
			return;
		}

		AggroTarget = null;
		_wasMovingToAggroTarget = false;
		PublishState();
	}

	private static void EmitBridgeEvent(string type, GDict payload)
	{
		TestBridge.Instance?.EmitEvent(type, payload);
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
