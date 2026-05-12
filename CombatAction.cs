#nullable enable

using System.Collections.Generic;

public enum CombatState
{
	OutOfCombat,
	Engaging,
	Ready,
	UsingAction,
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
		double cooldown,
		double castTime,
		double recoveryTime,
		bool requiresTarget,
		bool canUseWhileMoving,
		double damageMultiplier,
		bool usesBasicAttackCooldown)
	{
		ActionId = actionId;
		DisplayName = displayName;
		Kind = kind;
		Range = range;
		Cooldown = cooldown;
		CastTime = castTime;
		RecoveryTime = recoveryTime;
		RequiresTarget = requiresTarget;
		CanUseWhileMoving = canUseWhileMoving;
		DamageMultiplier = damageMultiplier;
		UsesBasicAttackCooldown = usesBasicAttackCooldown;
	}

	public string ActionId { get; }
	public string DisplayName { get; }
	public CombatActionKind Kind { get; }
	public double Range { get; }
	public double Cooldown { get; }
	public double CastTime { get; }
	public double RecoveryTime { get; }
	public bool RequiresTarget { get; }
	public bool CanUseWhileMoving { get; }
	public double DamageMultiplier { get; }
	public bool UsesBasicAttackCooldown { get; }

	public static CombatAction BasicAttack()
	{
		return new CombatAction(
			"basic_attack",
			"Basic Attack",
			CombatActionKind.BasicAttack,
			48.0,
			0.0,
			0.0,
			0.10,
			true,
			false,
			1.0,
			true);
	}
}

public readonly record struct CombatStats(
	int Attack,
	double Accuracy,
	int Defense,
	double Evasion,
	double AttacksPerSecond,
	int MaxHealth,
	int CurrentHealth);

public sealed class CombatantCombatSnapshot
{
	public CombatState State { get; init; }
	public string CurrentTargetName { get; init; } = string.Empty;
	public string QueuedActionId { get; init; } = string.Empty;
	public string ActiveActionId { get; init; } = string.Empty;
	public double BasicAttackCooldownRemaining { get; init; }
	public double CastRemaining { get; init; }
	public double RecoveryRemaining { get; init; }
	public IReadOnlyDictionary<string, double> SkillCooldowns { get; init; } = new Dictionary<string, double>();
	public bool IsDisabled { get; init; }
	public bool CanAct { get; init; }
}
