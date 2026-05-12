#nullable enable

using Godot;
using System.Collections.Generic;
using GDict = Godot.Collections.Dictionary;

public partial class Adventurer : Node2D, ICombatant
{
	[Export]
	public string AdventurerName { get; set; } = "Mira";

	[Export]
	public int Level { get; set; } = 1;

	[Export]
	public int MaxHealth { get; set; } = 28;

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
	public int CastTicksRemaining { get; private set; }
	public int RecoveryTicksRemaining { get; private set; }
	public IReadOnlyDictionary<string, int> SkillCooldowns => _skillCooldowns;
	public bool IsDisabled { get; private set; }
	public bool CanAct { get; private set; }

	private readonly Dictionary<string, int> _skillCooldowns = new();

	public override void _Ready()
	{
		Health = MaxHealth;
		Controller = GetNodeOrNull<AdventurerController>("AdventurerController");
		CombatController = GetNodeOrNull<AdventurerCombatController>("AdventurerCombatController");
		PublishState();
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
		QueueRedraw();
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
		QueueRedraw();
		PublishState();
	}

	public void SetCombatSnapshot(CombatantCombatSnapshot snapshot)
	{
		CombatState = !IsAlive ? global::CombatState.Defeated : snapshot.State;
		CurrentCombatTargetName = snapshot.CurrentTargetName;
		QueuedActionId = snapshot.QueuedActionId;
		ActiveActionId = snapshot.ActiveActionId;
		BasicAttackCooldownTicksRemaining = snapshot.BasicAttackCooldownTicksRemaining;
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
		Color bodyColor = IsAlive ? new Color(0.20f, 0.36f, 0.78f) : new Color(0.18f, 0.18f, 0.20f);
		DrawCircle(Vector2.Zero, 15.0f, bodyColor);
		DrawLine(new Vector2(-10.0f, 13.0f), new Vector2(10.0f, 13.0f), new Color(0.95f, 0.86f, 0.42f), 3.0f);
	}
}
