#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GDict = Godot.Collections.Dictionary;

public partial class Monster : Node2D, ICombatant
{
	private const string DefaultMonsterName = "Slime";

	[Export]
	public string MonsterName { get; set; } = DefaultMonsterName;

	[Export]
	public MonsterDefinition? Definition { get; set; }

	[Export]
	public CombatantRole Role { get; set; } = CombatantRole.DamageDealer;

	[Export]
	public int Level { get; set; } = 1;

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

	[Export]
	public EnemyGroupDefinition? EnemyGroup { get; set; }

	public int Health => Stats.CurrentHealth;
	public CombatState CombatState { get; private set; } = CombatState.OutOfCombat;
	public bool IsAlive => Health > 0;
	public string CombatantId => $"monster:{MonsterName}";
	public string CombatantKind => "monster";
	public string DisplayName => MonsterName;
	public CombatStats Stats { get; private set; }
	public string CurrentCombatTargetName { get; private set; } = string.Empty;
	public string QueuedActionId { get; private set; } = string.Empty;
	public string ActiveActionId { get; private set; } = string.Empty;
	public string LastActionId { get; private set; } = string.Empty;
	public string DefinitionId { get; private set; } = string.Empty;
	public string CombatLoadoutId { get; private set; } = string.Empty;
	public IReadOnlyList<string> ActionIds => _actionIds;
	public int BasicAttackCooldownTicksRemaining { get; private set; }
	public int GlobalCooldownTicksRemaining { get; private set; }
	public int CastTicksRemaining { get; private set; }
	public int RecoveryTicksRemaining { get; private set; }
	public IReadOnlyDictionary<string, int> SkillCooldowns => _skillCooldowns;
	public bool IsDisabled { get; private set; }
	public bool CanAct { get; private set; }
	public Adventurer? AggroTarget { get; private set; }
	public bool HasAggroTarget => AggroTarget?.IsAlive == true;
	public string EnemyGroupId { get; private set; } = string.Empty;
	public float SocialRadius { get; private set; }

	private readonly Dictionary<string, int> _skillCooldowns = new();
	private readonly List<string> _actionIds = new();
	private bool _hasSetup;
	private ProgressBar? _healthBar;
	private bool _wasMovingToAggroTarget;
	private Vector2 _homePosition;
	private CombatLoadout? _loadout;

	public override void _Ready()
	{
		if (!_hasSetup)
		{
			if (Definition is not null)
			{
				string? displayNameOverride = MonsterName == DefaultMonsterName ? null : MonsterName;
				SetupFromDefinition(Definition, displayNameOverride, Position);
			}
			else
			{
				throw new InvalidOperationException($"{nameof(Monster)} requires a definition or setup stats.");
			}
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
		if (!_hasSetup)
		{
			throw new InvalidOperationException($"{nameof(Monster)} starting stats are not available before setup.");
		}

		return Stats with { CurrentHealth = Stats.MaxHealth };
	}

	public void Setup(
		string? monsterName = null,
		CombatantRole? role = null,
		int? level = null,
		CombatStats? stats = null,
		Vector2? position = null,
		int? goldReward = null,
		int? experienceReward = null,
		float? speed = null,
		float? aggroRange = null,
		float? aggroAttackDistance = null,
		string definitionId = "")
	{
		MonsterName = monsterName ?? MonsterName;
		Role = role ?? Role;
		Level = level ?? Level;

		if (stats is CombatStats setupStats)
		{
			Stats = setupStats with
			{
				CurrentHealth = Mathf.Clamp(setupStats.CurrentHealth, 0, setupStats.MaxHealth)
			};
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
		Speed = speed ?? Speed;
		AggroRange = aggroRange ?? AggroRange;
		AggroAttackDistance = aggroAttackDistance ?? AggroAttackDistance;
		ApplyEnemyGroupDefinition();
		AggroTarget = null;
		_wasMovingToAggroTarget = false;
		CombatState = Health > 0 ? global::CombatState.OutOfCombat : global::CombatState.Defeated;
		CurrentCombatTargetName = string.Empty;
		QueuedActionId = string.Empty;
		ActiveActionId = string.Empty;
		LastActionId = string.Empty;
		DefinitionId = definitionId;
		CombatLoadoutId = string.Empty;
		_actionIds.Clear();
		BasicAttackCooldownTicksRemaining = 0;
		GlobalCooldownTicksRemaining = 0;
		CastTicksRemaining = 0;
		RecoveryTicksRemaining = 0;
		IsDisabled = false;
		CanAct = false;
		_skillCooldowns.Clear();
		_loadout = null;
		_hasSetup = true;
		UpdateHealthBar();

		if (IsInsideTree())
		{
			PublishState();
		}
	}

	public void ConfigureEnemyGroup(EnemyGroupDefinition? enemyGroup)
	{
		EnemyGroup = enemyGroup;
		ApplyEnemyGroupDefinition();

		if (IsInsideTree())
		{
			PublishState();
		}
	}

	public void SetupFromDefinition(
		MonsterDefinition definition,
		string? displayNameOverride = null,
		Vector2? position = null)
	{
		if (definition.Stats is null)
		{
			throw new InvalidOperationException($"Monster definition '{definition.DefinitionId}' is missing stats.");
		}

		Definition = definition;
		_loadout = definition.CombatLoadout?.ToRuntimeLoadout(definition.DefinitionId);
		Setup(
			monsterName: string.IsNullOrWhiteSpace(displayNameOverride) ? definition.DisplayName : displayNameOverride,
			role: definition.Role,
			level: definition.Level,
			stats: definition.Stats.ToRuntimeStats(),
			position: position,
			goldReward: definition.GoldReward,
			experienceReward: definition.ExperienceReward,
			speed: definition.MovementSpeed,
			aggroRange: definition.AggroRange,
			aggroAttackDistance: definition.AggroAttackDistance,
			definitionId: definition.DefinitionId);
		_loadout = definition.CombatLoadout?.ToRuntimeLoadout(definition.DefinitionId);

		if (GetNodeOrNull<Sprite2D>("Sprite2D") is Sprite2D sprite)
		{
			sprite.Modulate = definition.SpriteModulate;
		}
	}

	public int ApplyDamage(int amount)
	{
		if (!IsAlive)
		{
			return 0;
		}

		int previousHealth = Health;
		SetCurrentHealth(Mathf.Max(0, Health - Mathf.Max(0, amount)));

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
				{ "aggro_attack_distance", AggroAttackDistance },
				{ "enemy_group_id", EnemyGroupId },
				{ "social_radius", SocialRadius },
				{ "desired_combat_distance", GetDesiredCombatDistance() }
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
		_actionIds.Clear();
		_actionIds.AddRange(snapshot.ActionIds);
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
		RestoreStatsToFullHealth();
		Position = _homePosition;
		AggroTarget = null;
		_wasMovingToAggroTarget = false;
		SetCombatSnapshot(new CombatantCombatSnapshot
		{
			State = global::CombatState.OutOfCombat,
			DefinitionId = DefinitionId,
			CombatLoadoutId = CombatLoadoutId,
			ActionIds = _actionIds.ToArray()
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
			{ "role", Role.ToString() },
			{ "level", Level },
			{ "health", Health },
			{ "max_health", Stats.MaxHealth },
			{ "attack", Stats.Attack },
			{ "accuracy", Stats.Accuracy },
			{ "crit_chance", Stats.CritChance },
			{ "crit_damage", Stats.CritDamage },
			{ "defense", Stats.Defense },
			{ "evasion", Stats.Evasion },
			{ "initiative", Stats.Initiative },
			{ "attack_speed", Stats.AttackSpeedTicks },
			{ "speed", Speed },
			{ "aggro_range", AggroRange },
			{ "aggro_attack_distance", AggroAttackDistance },
			{ "enemy_group_id", EnemyGroupId },
			{ "social_radius", SocialRadius },
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
			{ "action_ids", BuildActionIdsState() },
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

		_healthBar.MaxValue = Mathf.Max(1, Stats.MaxHealth);
		_healthBar.Value = Mathf.Clamp(Health, 0, Stats.MaxHealth);
	}

	private void SetCurrentHealth(int health)
	{
		Stats = Stats with { CurrentHealth = Mathf.Clamp(health, 0, Stats.MaxHealth) };
	}

	private void RestoreStatsToFullHealth()
	{
		Stats = Stats with { CurrentHealth = Stats.MaxHealth };
	}

	private void ApplyEnemyGroupDefinition()
	{
		EnemyGroupId = EnemyGroup?.GroupId.Trim() ?? string.Empty;
		SocialRadius = Math.Max(0.0f, EnemyGroup?.SocialRadius ?? 0.0f);
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
		float desiredCombatDistance = GetDesiredCombatDistance();

		if (distance <= desiredCombatDistance)
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
					{ "aggro_attack_distance", AggroAttackDistance },
					{ "desired_combat_distance", desiredCombatDistance }
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
				{ "aggro_attack_distance", AggroAttackDistance },
				{ "desired_combat_distance", desiredCombatDistance }
			});
		}

		Vector2 movement = toTarget.Normalized() * Speed * (float)delta;
		GlobalPosition += movement.Length() >= distance - desiredCombatDistance
			? toTarget.Normalized() * Mathf.Max(0.0f, distance - desiredCombatDistance)
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

	private float GetDesiredCombatDistance()
	{
		return _loadout is CombatLoadout loadout
			? CombatPositioning.GetDesiredCombatDistance(this, loadout, AggroAttackDistance)
			: AggroAttackDistance;
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

	private Godot.Collections.Array BuildActionIdsState()
	{
		Godot.Collections.Array actionIds = new();

		foreach (string actionId in _actionIds)
		{
			actionIds.Add(actionId);
		}

		return actionIds;
	}
}
