#nullable enable

using Godot;

[GlobalClass]
public partial class CombatActionDefinition : Resource
{
	[Export]
	public string ActionId { get; set; } = string.Empty;

	[Export]
	public string DisplayName { get; set; } = string.Empty;

	[Export]
	public CombatActionKind Kind { get; set; } = CombatActionKind.BasicAttack;

	[Export]
	public double Range { get; set; } = 48.0;

	[Export]
	public int CooldownTicks { get; set; }

	[Export]
	public int CastTicks { get; set; }

	[Export]
	public int RecoveryTicks { get; set; }

	[Export]
	public bool RequiresTarget { get; set; } = true;

	[Export]
	public bool CanUseWhileMoving { get; set; }

	[Export]
	public int ActionWeight { get; set; }

	[Export]
	public double DamageMultiplier { get; set; } = 1.0;

	[Export]
	public bool UsesGlobalAttackCooldown { get; set; }

	public CombatAction ToRuntimeAction()
	{
		return new CombatAction(
			ActionId,
			DisplayName,
			Kind,
			Range,
			CooldownTicks,
			CastTicks,
			RecoveryTicks,
			RequiresTarget,
			CanUseWhileMoving,
			ActionWeight,
			DamageMultiplier,
			UsesGlobalAttackCooldown);
	}
}
