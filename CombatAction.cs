#nullable enable

using System.Collections.Generic;

public enum CombatState
{
	OutOfCombat,
	Engaging,
	Ready,
	Queued,
	Casting,
	Recovering,
	Disabled,
	Defeated
}

public enum CombatActionKind
{
	BasicAttack,
	Skill,
	Spell
}

public sealed class CombatAction
{
	public CombatAction(
		string actionId,
		string displayName,
		CombatActionKind kind,
		double range,
		int cooldownTicks,
		int castTicks,
		int recoveryTicks,
		bool requiresTarget,
		bool canUseWhileMoving,
		int actionWeight,
		double damageMultiplier,
		bool usesGlobalAttackCooldown)
	{
		ActionId = actionId;
		DisplayName = displayName;
		Kind = kind;
		Range = range;
		CooldownTicks = cooldownTicks;
		CastTicks = castTicks;
		RecoveryTicks = recoveryTicks;
		RequiresTarget = requiresTarget;
		CanUseWhileMoving = canUseWhileMoving;
		ActionWeight = actionWeight;
		DamageMultiplier = damageMultiplier;
		UsesGlobalAttackCooldown = usesGlobalAttackCooldown;
	}

	public string ActionId { get; }
	public string DisplayName { get; }
	public CombatActionKind Kind { get; }
	public double Range { get; }
	public int CooldownTicks { get; }
	public int CastTicks { get; }
	public int RecoveryTicks { get; }
	public bool RequiresTarget { get; }
	public bool CanUseWhileMoving { get; }
	public int ActionWeight { get; }
	public double DamageMultiplier { get; }
	public bool UsesGlobalAttackCooldown { get; }

	public static CombatAction BasicAttack(double range = 48.0)
	{
		return new CombatAction(
			"basic_attack",
			"Basic Attack",
			CombatActionKind.BasicAttack,
			range,
			0,
			0,
			0,
			true,
			false,
			0,
			1.0,
			true);
	}
}

public readonly record struct CombatStats(
	int Attack,
	double Accuracy,
	int Defense,
	double Evasion,
	int Initiative,
	int AttackSpeedTicks,
	int MaxHealth,
	int CurrentHealth);

public sealed class CombatantCombatSnapshot
{
	public CombatState State { get; init; }
	public string CurrentTargetName { get; init; } = string.Empty;
	public string QueuedActionId { get; init; } = string.Empty;
	public string ActiveActionId { get; init; } = string.Empty;
	public string LastActionId { get; init; } = string.Empty;
	public int BasicAttackCooldownTicksRemaining { get; init; }
	public int GlobalCooldownTicksRemaining { get; init; }
	public int CastTicksRemaining { get; init; }
	public int RecoveryTicksRemaining { get; init; }
	public IReadOnlyDictionary<string, int> SkillCooldowns { get; init; } = new Dictionary<string, int>();
	public bool IsDisabled { get; init; }
	public bool CanAct { get; init; }
}
