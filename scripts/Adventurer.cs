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
	[Export]
	public string AdventurerName { get; set; } = "Mira";

	[Export]
	public AdventurerDefinition? Definition { get; set; }

	[Export]
	public AdventurerArchetype Archetype { get; set; } = AdventurerArchetype.Warrior;

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
	public string CombatantId => $"adventurer:{AdventurerName}";
	public string CombatantKind => "adventurer";
	public string DisplayName => AdventurerName;
	public CombatStats Stats => new(Attack, Accuracy, Defense, Evasion, Initiative, AttackSpeed, MaxHealth, Health);
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
				SetupFromDefinition(Definition, position: Position);
			}
			else
			{
				Health = MaxHealth;
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
		return new CombatStats(Attack, Accuracy, Defense, Evasion, Initiative, AttackSpeed, MaxHealth, MaxHealth);
	}

	public void Setup(
		string? adventurerName = null,
		AdventurerArchetype? archetype = null,
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

	public int Heal(int amount)
	{
		if (!IsAlive || amount <= 0)
		{
			return 0;
		}

		int previousHealth = Health;
		Health = Mathf.Min(MaxHealth, Health + amount);
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
			{ "archetype", Archetype.ToString() },
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
