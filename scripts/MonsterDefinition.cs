#nullable enable

using Godot;

[GlobalClass]
public partial class MonsterDefinition : Resource
{
	[Export]
	public string DefinitionId { get; set; } = string.Empty;

	[Export]
	public string DisplayName { get; set; } = string.Empty;

	[Export]
	public CombatantRole Role { get; set; } = CombatantRole.DamageDealer;

	[Export]
	public int Level { get; set; } = 1;

	[Export]
	public CombatStatsDefinition? Stats { get; set; }

	[Export]
	public CombatLoadoutDefinition? CombatLoadout { get; set; }

	[Export]
	public int GoldReward { get; set; }

	[Export]
	public int ExperienceReward { get; set; }

	[Export]
	public float MovementSpeed { get; set; } = 90.0f;

	[Export]
	public float AggroRange { get; set; } = 48.0f;

	[Export]
	public float AggroAttackDistance { get; set; } = 42.0f;

	[Export]
	public Color SpriteModulate { get; set; } = Colors.White;
}
