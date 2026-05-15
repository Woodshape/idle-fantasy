#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using GDict = Godot.Collections.Dictionary;

public enum AdventurerArchetype
{
	Warrior,
	Mage
}

public partial class Adventurer : Node2D, ICombatant
{
	private const string DefaultAdventurerName = "Mira";

	[Export]
	public string AdventurerName { get; set; } = DefaultAdventurerName;

	[Export]
	public AdventurerDefinition? Definition { get; set; }

	[Export]
	public AdventurerArchetype Archetype { get; set; } = AdventurerArchetype.Warrior;

	[Export]
	public CombatantRole Role { get; set; } = CombatantRole.Tank;

	[Export]
	public int Level { get; set; } = 1;

	[Export]
	public float Speed { get; set; } = 120.0f;

	[Export]
	public float StopDistance { get; set; } = 8.0f;

	public int Experience { get; private set; }
	public int TotalExperience { get; private set; }
	public int XpToNextLevel => GetExperienceThresholdForLevel(Level);
	public int Gold { get; private set; }
	public int Health => Stats.CurrentHealth;
	public Vector2? MoveTarget { get; private set; }
	public Monster? CurrentMonsterTarget { get; private set; }
	public AdventurerController? Controller { get; private set; }
	public AdventurerCombatController? CombatController { get; private set; }
	public bool IsAlive => Health > 0;
	public string IntentionStateName => Controller?.State.ToString() ?? "Unknown";
	public string CombatStateName => CombatState.ToString();
	public string CombatantId => $"adventurer:{AdventurerName}";
	public string CombatantKind => "adventurer";
	public string DisplayName => AdventurerName;
	public CombatStats Stats { get; private set; }
	public CombatState CombatState { get; private set; } = CombatState.OutOfCombat;
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

	private readonly Dictionary<string, int> _skillCooldowns = new();
	private readonly List<string> _actionIds = new();
	private bool _hasSetup;
	private ProgressBar? _healthBar;
	private Line2D? _selectionOutline;

	public override void _Ready()
	{
		if (!_hasSetup)
		{
			if (Definition is not null)
			{
				string? displayNameOverride = AdventurerName == DefaultAdventurerName ? null : AdventurerName;
				SetupFromDefinition(Definition, displayNameOverride, Position);
			}
			else
			{
				throw new InvalidOperationException($"{nameof(Adventurer)} requires a definition or setup stats.");
			}
		}

		Controller = GetNodeOrNull<AdventurerController>("AdventurerController");
		CombatController = GetNodeOrNull<AdventurerCombatController>("AdventurerCombatController");
		_healthBar = GetNodeOrNull<ProgressBar>("HealthBar");
		EnsureSelectionOutline();
		UpdateHealthBar();
		PublishState();
	}

	public void SetSelected(bool selected)
	{
		EnsureSelectionOutline();

		if (_selectionOutline is not null)
		{
			_selectionOutline.Visible = selected;
		}
	}

	public CombatStats CreateStartingStats()
	{
		if (!_hasSetup)
		{
			throw new InvalidOperationException($"{nameof(Adventurer)} starting stats are not available before setup.");
		}

		return Stats with { CurrentHealth = Stats.MaxHealth };
	}

	public void Setup(
		string? adventurerName = null,
		AdventurerArchetype? archetype = null,
		CombatantRole? role = null,
		int? level = null,
		CombatStats? stats = null,
		Vector2? position = null,
		float? speed = null,
		float? stopDistance = null,
		int experience = 0,
		int gold = 0,
		string definitionId = "")
	{
		AdventurerName = adventurerName ?? AdventurerName;
		Archetype = archetype ?? Archetype;
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
			throw new InvalidOperationException($"{nameof(Adventurer)}.{nameof(Setup)} requires character stats.");
		}

		if (position is Vector2 setupPosition)
		{
			Position = setupPosition;
		}

		Speed = speed ?? Speed;
		StopDistance = stopDistance ?? StopDistance;
		DefinitionId = definitionId;
		Experience = Math.Max(0, experience);
		TotalExperience = Experience;
		ProcessLevelUps(Experience);
		Gold = gold;
		MoveTarget = null;
		CurrentMonsterTarget = null;
		CombatState = Health > 0 ? global::CombatState.OutOfCombat : global::CombatState.Defeated;
		CurrentCombatTargetName = string.Empty;
		QueuedActionId = string.Empty;
		ActiveActionId = string.Empty;
		LastActionId = string.Empty;
		CombatLoadoutId = string.Empty;
		_actionIds.Clear();
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

	public void SetupFromDefinition(
		AdventurerDefinition definition,
		string? displayNameOverride = null,
		Vector2? position = null)
	{
		if (definition.Stats is null)
		{
			throw new InvalidOperationException($"Adventurer definition '{definition.DefinitionId}' is missing stats.");
		}

		Definition = definition;
		Setup(
			adventurerName: string.IsNullOrWhiteSpace(displayNameOverride) ? definition.DisplayName : displayNameOverride,
			archetype: ParseArchetype(definition.ArchetypeId),
			role: definition.Role,
			level: definition.Level,
			stats: definition.Stats.ToRuntimeStats(),
			position: position,
			speed: definition.MovementSpeed,
			stopDistance: definition.StopDistance,
			experience: definition.StartingExperience,
			gold: definition.StartingGold,
			definitionId: definition.DefinitionId);

		if (GetNodeOrNull<Sprite2D>("Sprite2D") is Sprite2D sprite)
		{
			sprite.Modulate = definition.SpriteModulate;
		}
	}

	public void SetMoveTarget(Vector2 target)
	{
		MoveTarget = target;
		PublishState();
	}

	public bool ClearMoveTarget()
	{
		if (MoveTarget is null)
		{
			return false;
		}

		MoveTarget = null;
		PublishState();
		return true;
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
		SetCurrentHealth(Mathf.Max(0, Health - Mathf.Max(0, amount)));
		UpdateHealthBar();
		PublishState();
		return previousHealth - Health;
	}

	public void AddRewards(int gold, int experience)
	{
		Gold += gold;
		int earnedExperience = Math.Max(0, experience);
		Experience += earnedExperience;
		TotalExperience += earnedExperience;
		ProcessLevelUps(earnedExperience);
		PublishState();
	}

	public bool SpendGold(int amount)
	{
		int spendAmount = Math.Max(0, amount);

		if (spendAmount == 0)
		{
			return true;
		}

		if (Gold < spendAmount)
		{
			return false;
		}

		Gold -= spendAmount;
		PublishState();
		return true;
	}

	public void RecoverToFull()
	{
		RestoreStatsToFullHealth();
		UpdateHealthBar();
		PublishState();
	}

	public int Heal(int amount)
	{
		if (!IsAlive || amount <= 0)
		{
			return 0;
		}

		int previousHealth = Health;
		SetCurrentHealth(Mathf.Min(Stats.MaxHealth, Health + amount));
		UpdateHealthBar();
		PublishState();
		return Health - previousHealth;
	}

	public void SetCombatSnapshot(CombatantCombatSnapshot snapshot)
	{
		CombatState = !IsAlive ? global::CombatState.Defeated : snapshot.State;
		CurrentCombatTargetName = snapshot.CurrentTargetName;
		QueuedActionId = snapshot.QueuedActionId;
		ActiveActionId = snapshot.ActiveActionId;
		LastActionId = snapshot.State == global::CombatState.OutOfCombat
			? string.Empty
			: snapshot.LastActionId;
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
			{ "combatant_id", CombatantId },
			{ "archetype", Archetype.ToString() },
			{ "role", Role.ToString() },
			{ "level", Level },
			{ "experience", Experience },
			{ "current_experience", Experience },
			{ "xp_to_next_level", XpToNextLevel },
			{ "total_experience", TotalExperience },
			{ "gold", Gold },
			{ "health", Health },
			{ "max_health", Stats.MaxHealth },
			{ "attack", Stats.Attack },
			{ "accuracy", Stats.Accuracy },
			{ "defense", Stats.Defense },
			{ "evasion", Stats.Evasion },
			{ "initiative", Stats.Initiative },
			{ "attack_speed", Stats.AttackSpeedTicks },
			{ "speed", Speed },
			{ "is_alive", IsAlive },
			{ "intention_state", IntentionStateName },
			{ "combat_state", CombatStateName },
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
		TestBridge.Instance.EmitState(CombatantId, state);

		string definitionStateName = string.IsNullOrWhiteSpace(DefinitionId)
			? $"adventurer:{AdventurerName}"
			: $"adventurer:{DefinitionId}";
		TestBridge.Instance.EmitState(definitionStateName, state);
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

	private void ProcessLevelUps(int earnedExperience)
	{
		while (Experience >= XpToNextLevel)
		{
			int oldLevel = Level;
			int threshold = XpToNextLevel;
			int xpBeforeLevelUp = Experience;
			CombatStats statsBefore = Stats;

			Level++;
			Experience -= threshold;

			StatGrowth growth = GetGrowthForRole(Role, Level);
			ApplyGrowth(growth);
			UpdateHealthBar();

			EmitLevelUpEvent(
				oldLevel,
				Level,
				xpBeforeLevelUp,
				Experience,
				threshold,
				earnedExperience,
				growth,
				statsBefore,
				Stats);
		}
	}

	private void ApplyGrowth(StatGrowth growth)
	{
		int maxHealth = Stats.MaxHealth + growth.MaxHealth;
		int currentHealth = IsAlive
			? Mathf.Min(maxHealth, Stats.CurrentHealth + Math.Max(0, growth.MaxHealth))
			: Stats.CurrentHealth;

		Stats = Stats with
		{
			MaxHealth = maxHealth,
			CurrentHealth = currentHealth,
			Attack = Stats.Attack + growth.Attack,
			Defense = Stats.Defense + growth.Defense
		};
	}

	private void EmitLevelUpEvent(
		int oldLevel,
		int newLevel,
		int xpBeforeLevelUp,
		int xpAfterLevelUp,
		int threshold,
		int earnedExperience,
		StatGrowth growth,
		CombatStats statsBefore,
		CombatStats statsAfter)
	{
		TestBridge.Instance?.EmitEvent("adventurer_level_up", new GDict
		{
			{ "source", nameof(Adventurer) },
			{ "adventurer", AdventurerName },
			{ "definition_id", DefinitionId },
			{ "role", Role.ToString() },
			{ "old_level", oldLevel },
			{ "new_level", newLevel },
			{ "xp_before", xpBeforeLevelUp },
			{ "xp_after", xpAfterLevelUp },
			{ "threshold", threshold },
			{ "xp_to_next_level", XpToNextLevel },
			{ "earned_experience", earnedExperience },
			{ "total_experience", TotalExperience },
			{ "changed_stats", new GDict
				{
					{ "max_health", growth.MaxHealth },
					{ "current_health", statsAfter.CurrentHealth - statsBefore.CurrentHealth },
					{ "attack", growth.Attack },
					{ "defense", growth.Defense }
				}
			},
			{ "stats_before", BuildStatsState(statsBefore) },
			{ "stats_after", BuildStatsState(statsAfter) }
		});
	}

	private static int GetExperienceThresholdForLevel(int level)
	{
		return 20 + ((Math.Max(1, level) - 1) * 15);
	}

	private static StatGrowth GetGrowthForRole(CombatantRole role, int newLevel)
	{
		return role switch
		{
			CombatantRole.Tank => new StatGrowth(6, 1, 1),
			CombatantRole.Support => new StatGrowth(5, 1, newLevel % 2 == 0 ? 1 : 0),
			_ => new StatGrowth(4, 2, newLevel % 2 == 0 ? 1 : 0)
		};
	}

	private static GDict BuildStatsState(CombatStats stats)
	{
		return new GDict
		{
			{ "max_health", stats.MaxHealth },
			{ "current_health", stats.CurrentHealth },
			{ "attack", stats.Attack },
			{ "defense", stats.Defense },
			{ "accuracy", stats.Accuracy },
			{ "evasion", stats.Evasion },
			{ "initiative", stats.Initiative },
			{ "attack_speed", stats.AttackSpeedTicks }
		};
	}

	private readonly record struct StatGrowth(int MaxHealth, int Attack, int Defense);

	private void EnsureSelectionOutline()
	{
		if (_selectionOutline is not null)
		{
			return;
		}

		_selectionOutline = GetNodeOrNull<Line2D>("SelectionOutline");

		if (_selectionOutline is null)
		{
			_selectionOutline = new Line2D
			{
				Name = "SelectionOutline",
				Closed = true,
				Width = 2.0f,
				DefaultColor = new Color(1.0f, 0.92f, 0.35f, 0.9f),
				ZIndex = 10,
				Visible = false
			};

			const int pointCount = 32;
			const float radius = 21.0f;

			for (int index = 0; index < pointCount; index++)
			{
				float angle = Mathf.Tau * index / pointCount;
				_selectionOutline.AddPoint(new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
			}

			AddChild(_selectionOutline);
		}

		_selectionOutline.Visible = false;
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

	private static AdventurerArchetype ParseArchetype(string archetypeId)
	{
		return string.Equals(archetypeId, "mage", StringComparison.OrdinalIgnoreCase)
			? AdventurerArchetype.Mage
			: AdventurerArchetype.Warrior;
	}
}
