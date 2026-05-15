#nullable enable

using Godot;

public enum CombatantRole
{
	Tank,
	DamageDealer,
	Support
}

[GlobalClass]
public partial class AdventurerDefinition : Resource
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
	public float MovementSpeed { get; set; } = 120.0f;

	[Export]
	public float StopDistance { get; set; } = 8.0f;

	[Export]
	public CombatLoadoutDefinition? CombatLoadout { get; set; }

	[Export]
	public Color SpriteModulate { get; set; } = Colors.White;

	[Export]
	public int StartingGold { get; set; }

	[Export]
	public int StartingExperience { get; set; }
}
